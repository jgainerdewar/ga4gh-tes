﻿// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Linq;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Tes.Models;

namespace Tes.TaskSubmitters
{
    /// <summary>
    /// Cromwell workflow engine metadata.
    /// </summary>
    public partial class CromwellTaskSubmitter() : TaskSubmitter(SubmitterName)
    {
        public const string SubmitterName = "cromwell";

        // Parses out the task and shard along with its workflow name & id from the execution path. Note that is expected to be the most deeply nested workflow.
        [GeneratedRegex("/*?/(.+)/([^/]+)/([0-9A-Fa-f]{8}[-][0-9A-Fa-f]{4}[-][0-9A-Fa-f]{4}[-][0-9A-Fa-f]{4}[-][0-9A-Fa-f]{12})/call-([^/]+)(?:/shard-([^/]+))?(?:/attempt-([^/]+))?/execution/rc", RegexOptions.Singleline)]
        // examples: /cromwell-executions/test/daf1a044-d741-4db9-8eb5-d6fd0519b1f1/call-hello/execution/rc
        // examples: /cromwell-executions/test/daf1a044-d741-4db9-8eb5-d6fd0519b1f1/call-hello/test-subworkflow/b5227f73-f6e8-43be-8b18-520b1fd789b6/call-subworkflow/shard-8/execution/rc
        // examples: /cromwell-executions/test/daf1a044-d741-4db9-8eb5-d6fd0519b1f1/call-hello/test-subworkflow/b5227f73-f6e8-43be-8b18-520b1fd789b6/call-subworkflow/shard-8/attempt-2/execution/rc
        private static partial Regex CromwellPathRegex();

        // Parses the task instance name from the description
        [GeneratedRegex("(.*):[^:]*:[^:]*", RegexOptions.Singleline)]
        private static partial Regex GetCromwellTaskInstanceNameRegex();

        // Parses the shard from the description
        [GeneratedRegex(".*:([^:]*):[^:]*", RegexOptions.Singleline)]
        private static partial Regex GetCromwellShardRegex();

        // Parses the attempt from the description
        [GeneratedRegex(".*:([^:]*)", RegexOptions.Singleline)]
        private static partial Regex GetCromwellAttemptRegex();

        private static readonly Regex cromwellTaskInstanceNameRegex = GetCromwellTaskInstanceNameRegex();
        private static readonly Regex cromwellShardRegex = GetCromwellShardRegex();
        private static readonly Regex cromwellAttemptRegex = GetCromwellAttemptRegex();
        private static readonly Regex cromwellPathRegex = CromwellPathRegex();

        /// <summary>
        /// Determines if the <see cref="TesInput"/> file is a Cromwell command script
        /// See https://github.com/broadinstitute/cromwell/blob/17efd599d541a096dc5704991daeaefdd794fefd/supportedBackends/tes/src/main/scala/cromwell/backend/impl/tes/TesTask.scala#L58
        /// </summary>
        /// <param name="inputFile"><see cref="TesInput"/> file</param>
        /// <returns>True if the input represents a Cromwell command script</returns>
        private static bool IsCromwellCommandScript(TesInput inputFile) =>
            inputFile.Type == TesFileType.FILE &&
            (inputFile.Name?.Equals("commandScript", StringComparison.Ordinal) ?? false)
            && (inputFile.Description?.EndsWith(".commandScript") ?? false)
            && inputFile.Path.EndsWith($"/script");

        internal static new CromwellTaskSubmitter Parse(TesTask task)
        {
            if (string.IsNullOrWhiteSpace(task.Description) || !(task.Inputs?.Any(IsCromwellCommandScript) ?? false))
            {
                return null;
            }

            var descriptionWorkflowId = Guid.Parse(task.Description.Split(':')[0]);
            TesOutput rcOutput = default;
            var hasStdErrOutput = false;
            var hasStdOutOutput = false;

            foreach (var output in task.Outputs ?? [])
            {
                if (output.Name.Equals("rc", StringComparison.Ordinal))
                {
                    rcOutput = output;
                }
                else
                {
                    hasStdErrOutput |= output.Name.Equals("stderr", StringComparison.Ordinal);
                    hasStdOutOutput |= output.Name.Equals("stdout", StringComparison.Ordinal);
                }
            }

            if (hasStdErrOutput && hasStdOutOutput && rcOutput is not null && !rcOutput.Path.Contains('\n'))
            {
                var path = rcOutput.Path.Split('/');
                // path[0] <= string.Empty
                // path[1] <= cromwell executions root directory (without beginning or ending '/')
                // path[2] <= top workflow name
                // path[3] <= top workflow id

                var match = cromwellPathRegex.Match(rcOutput.Path);
                // match.Groups[1] <= execution directory path from root to deepest workflow name (not including beginning or ending '/')
                // match.Groups[2] <= subworkflow name, possibly prefixed with its parent workflow name separated by '-'
                // match.Groups[3] <= subworkflow id
                // match.Groups[4] <= task name
                // match.Groups[5] <= shard, if present

                if (match.Success && match.Captures.Count == 1 && match.Groups.Count == 7)
                {
                    var workflowName = path[2];
                    var workflowId = path[3]; // This is how we set WorkflowId before making this pre-submitter determinable
                    var subWorkflowId = match.Groups[3].Value;

                    if (Guid.TryParse(subWorkflowId, out var workflowIdAsGuid) && descriptionWorkflowId.Equals(workflowIdAsGuid))
                    {
                        return new()
                        {
                            WorkflowId = workflowId,
                            WorkflowName = workflowName,
                            CromwellTaskInstanceName = cromwellTaskInstanceNameRegex.Match(task.Description).Groups[1].Value,
                            CromwellShard = int.TryParse(cromwellShardRegex.Match(task.Description).Groups[1].Value, out var shard) ? shard : null,
                            CromwellAttempt = int.TryParse(cromwellAttemptRegex.Match(task.Description).Groups[1].Value, out var attempt) ? attempt : null,
                            CromwellExecutionDir = string.Join('/', path.Take(path.Length - 1)),
                            CromwellRcUri = rcOutput.Url,
                        };
                    }
                }
            }

            return null;
        }

        /// <summary>
        /// Workflow name.
        /// </summary>
        [JsonPropertyName("cromwellWorkflowName")]
        public string WorkflowName { get; set; }

        /// <summary>
        /// Cromwell task description without shard and attempt numbers
        /// </summary>
        [JsonPropertyName("cromwellTaskInstanceName")]
        public string CromwellTaskInstanceName { get; set; }

        /// <summary>
        /// Cromwell shard number
        /// </summary>
        [JsonPropertyName("cromwellShard")]
        public int? CromwellShard { get; set; }

        /// <summary>
        /// Cromwell attempt number
        /// </summary>
        [JsonPropertyName("cromwellAttempt")]
        public int? CromwellAttempt { get; set; }

        /// <summary>
        /// Cromwell task execution directory
        /// </summary>
        [JsonPropertyName("cromwellExecutionDir")]
        public string CromwellExecutionDir { get; set; }

        /// <summary>
        /// Cromwell task execution rc file url
        /// </summary>
        [JsonPropertyName("cromwellRcUrl")]
        public string CromwellRcUri { get; set; }
    }
}
