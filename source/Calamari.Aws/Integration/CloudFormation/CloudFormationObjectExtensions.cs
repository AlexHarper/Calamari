﻿using System;
using System.Collections.Generic;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Amazon.CloudFormation;
using Amazon.CloudFormation.Model;
using Amazon.Runtime;
using Calamari.Aws.Deployment.Conventions;
using Calamari.Aws.Exceptions;
using Calamari.Aws.Util;
using Octopus.CoreUtilities;
using Octopus.CoreUtilities.Extensions;
using StackStatus = Calamari.Aws.Deployment.Conventions.StackStatus;

namespace Calamari.Aws.Integration.CloudFormation
{
    public static class CloudFormationObjectExtensions
    {
        // These status indicate that an update or create was not successful.
        // http://docs.aws.amazon.com/AWSCloudFormation/latest/UserGuide/using-cfn-describing-stacks.html#w2ab2c15c15c17c11
        private static HashSet<string> UnsuccessfulStackEvents =
            new HashSet<string>(StringComparer.InvariantCultureIgnoreCase)
            {
                "CREATE_ROLLBACK_COMPLETE",
                "CREATE_ROLLBACK_FAILED",
                "UPDATE_ROLLBACK_COMPLETE",
                "UPDATE_ROLLBACK_FAILED",
                "ROLLBACK_COMPLETE",
                "ROLLBACK_FAILED",
                "DELETE_FAILED",
                "CREATE_FAILED"
            };
    
        public static DescribeChangeSetResponse DescribeChangeSet(this Func<AmazonCloudFormationClient> factory, StackArn stack, ChangeSetArn changeSet)
        {
            return factory().Map(client => client.DescribeChangeSet(new DescribeChangeSetRequest
            {
                ChangeSetName = stack.Value,
                StackName = changeSet.Value
            }));
        }

        public static DescribeChangeSetResponse WaitForChangeSetCompletion(this Func<AmazonCloudFormationClient> clientFactory, TimeSpan waitPeriod, RunningChangeset runningChangeset)
        {
            var completion = new HashSet<ChangeSetStatus> { ChangeSetStatus.FAILED, ChangeSetStatus.CREATE_COMPLETE, ChangeSetStatus.DELETE_COMPLETE };
            while (true)
            {
                var result = clientFactory.DescribeChangeSet(runningChangeset.Stack, runningChangeset.ChangeSet);
                if (completion.Contains(result.Status))
                {
                    return result;
                }

                Thread.Sleep(waitPeriod);
            }
        }

        /// <summary>
        /// Gets the last stack event by timestamp, optionally filtered by a predicate
        /// </summary>
        /// <param name="predicate">The optional predicate used to filter events</param>
        /// <returns>The stack event</returns>
        public static StackEvent GetLastStackEvent(this Func<AmazonCloudFormationClient> clientFactory, StackArn stack, Func<StackEvent, bool> predicate = null)
        {
            var client = clientFactory();

            return client.DescribeStackEvents(new DescribeStackEventsRequest { StackName = stack.Value })
                .Map(response => response?.StackEvents
                    .OrderByDescending(stackEvent => stackEvent.Timestamp)
                    .FirstOrDefault(stackEvent => predicate == null || predicate(stackEvent))
                );
        }

        public static Stack DescribeStack(this Func<AmazonCloudFormationClient> clientFactory, StackArn stack)
        {
            return clientFactory()
                // The client becomes the result of the API call
                .Map(client => client.DescribeStacks(new DescribeStacksRequest { StackName = stack.Value }))
                // Get the first stack
                .Map(response => response.Stacks.FirstOrDefault());
        }


        /// <summary>
        /// Check to see if the stack name exists.
        /// </summary>
        /// <param name="defaultValue">The return value when the user does not have the permissions to query the stacks</param>
        /// <returns>The current status of the stack</returns>
        public static StackStatus GetStackStatus(this Func<AmazonCloudFormationClient> clientFactory, StackArn stackArn,
            StackStatus defaultValue)
        {
            try
            {
                return clientFactory.DescribeStack(stackArn)
                           // Does the status indicate that processing has finished?
                           ?.Map(stack => (stack.StackStatus?.Value.EndsWith("_COMPLETE") ?? true) ||
                                          stack.StackStatus.Value.EndsWith("_FAILED"))
                           .Map(completed => completed ? StackStatus.Completed : StackStatus.InProgress)
                       ?? StackStatus.DoesNotExist;
            }
            catch (AmazonCloudFormationException ex)
            {
                if (ex.ErrorCode == "AccessDenied")
                {
                    return defaultValue;
                }

                // This is OK, we just return the fact that the stack does not exist.
                // While calling describe stacks and catching exceptions seems dirty,
                // this is how the stack-exists command on the CLI works:
                // https://docs.aws.amazon.com/cli/latest/reference/cloudformation/wait/stack-exists.html
                if (ex.ErrorCode == "ValidationError")
                {
                    return StackStatus.DoesNotExist;
                }

                throw new UnknownException(
                    "AWS-CLOUDFORMATION-ERROR-0006: An unrecognised exception was thrown while checking to see if the CloudFormation stack exists.\n" +
                    "For more information visit https://g.octopushq.com/AwsCloudFormationDeploy#aws-cloudformation-error-0006",
                    ex);
            }
        }

        /// <summary>
        /// Get the potentially useful inner web exception message if there is one.
        /// </summary>
        /// <param name="exception"></param>
        /// <returns></returns>
        public static string GetWebExceptionMessage(this AmazonServiceException exception)
        {
            return (exception.InnerException as WebException)?
                    .Response?
                    .GetResponseStream()?
                    .Map(stream => new StreamReader(stream).ReadToEnd())
                    .Map(message => "An exception was thrown while contacting the AWS API.\n" + message)
                    ?? "An exception was thrown while contacting the AWS API.";
        }

        public static void WaitForStackToComplete(this Func<AmazonCloudFormationClient> clientFactory, TimeSpan waitPeriod, StackArn stack)
        {
            var status = clientFactory.GetStackStatus(stack, StackStatus.DoesNotExist);
            if (status == StackStatus.DoesNotExist || status == StackStatus.Completed)
            {
                return;
            }

            do
            {
                Thread.Sleep(waitPeriod);
                clientFactory.GetLastStackEvent(stack);
            } while (clientFactory.GetStackStatus(stack, StackStatus.Completed) == StackStatus.InProgress);
        }

        private static void StackEventCompleted(bool expectSuccess = true, bool missingIsFailure = true)
        {
            
        }
        
        /// <summary>
        /// Check the stack event status to detemrine whether it was successful.
        /// http://docs.aws.amazon.com/AWSCloudFormation/latest/UserGuide/using-cfn-describing-stacks.html#w2ab2c15c15c17c11
        /// </summary>
        /// <param name="status">The status to check</param>
        /// <returns>true if the status indcates a failed create or update, and false otherwise</returns>
        private static Maybe<bool> IsStackEventSuccessful(Maybe<StackEvent> status)
        {
            return status.Select(x=> !UnsuccessfulStackEvents.Contains(x.ResourceStatus.Value));
        }
        
    }
}
