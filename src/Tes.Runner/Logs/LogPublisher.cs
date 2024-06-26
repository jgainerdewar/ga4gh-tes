﻿// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Azure.Storage.Sas;
using Tes.Runner.Models;
using Tes.Runner.Storage;

namespace Tes.Runner.Logs
{
    public class LogPublisher
    {
        const BlobSasPermissions LogLocationPermissions = BlobSasPermissions.Read | BlobSasPermissions.Create | BlobSasPermissions.Write | BlobSasPermissions.Add;

        public static async Task<IStreamLogReader> CreateStreamReaderLogPublisherAsync(NodeTask nodeTask, string logNamePrefix, string apiVersion)
        {
            ArgumentNullException.ThrowIfNull(nodeTask);
            ArgumentException.ThrowIfNullOrEmpty(logNamePrefix);

            if (!string.IsNullOrWhiteSpace(nodeTask.RuntimeOptions?.StreamingLogPublisher?.TargetUrl))
            {
                var transformedUrl = await UrlTransformationStrategyFactory.GetTransformedUrlAsync(
                    nodeTask.RuntimeOptions,
                    nodeTask.RuntimeOptions.StreamingLogPublisher,
                    LogLocationPermissions,
                    apiVersion);

                return new AppendBlobLogPublisher(
                    transformedUrl.ToString()!,
                    logNamePrefix);
            }

            return new ConsoleStreamLogPublisher();
        }
    }
}
