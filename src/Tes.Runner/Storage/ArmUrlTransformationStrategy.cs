﻿// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Sas;
using Microsoft.Extensions.Logging;
using Tes.Runner.Models;
using Tes.Runner.Transfer;

namespace Tes.Runner.Storage
{
    public class ArmUrlTransformationStrategy : IUrlTransformationStrategy
    {
        const string BlobUrlPrefix = ".blob."; // "core.windows.net";
        private const int BlobSasTokenExpirationInHours = 24 * 7; //7 days which is the Azure Batch node runtime;
        const int UserDelegationKeyExpirationInHours = 1;

        private readonly ILogger logger = PipelineLoggerFactory.Create<ArmUrlTransformationStrategy>();
        private readonly Dictionary<string, UserDelegationKey> userDelegationKeyDictionary = [];
        private readonly SemaphoreSlim semaphoreSlim = new(1, 1);
        private readonly Func<Uri, BlobServiceClient> blobServiceClientFactory;
        private readonly RuntimeOptions runtimeOptions;
        private readonly string storageHostSuffix;

        public ArmUrlTransformationStrategy(Func<Uri, BlobServiceClient> blobServiceClientFactory, RuntimeOptions runtimeOptions)
        {
            ArgumentNullException.ThrowIfNull(blobServiceClientFactory);
            ArgumentNullException.ThrowIfNull(runtimeOptions);

            this.blobServiceClientFactory = blobServiceClientFactory;
            this.runtimeOptions = runtimeOptions;
            storageHostSuffix = BlobUrlPrefix + this.runtimeOptions!.AzureEnvironmentConfig!.StorageUrlSuffix;
        }

        public async Task<Uri> TransformUrlWithStrategyAsync(string sourceUrl, BlobSasPermissions blobSasPermissions)
        {
            ArgumentNullException.ThrowIfNull(sourceUrl);

            if (!IsValidAzureStorageAccountUri(sourceUrl))
            {
                var uri = new Uri(sourceUrl);
                logger.LogWarning("The URL provided is not a valid storage account. The resolution strategy won't be applied. Host: {Host}", uri.Host);

                return uri;
            }

            if (BlobApiHttpUtils.UrlContainsSasToken(sourceUrl))
            {
                var uri = new Uri(sourceUrl);
                logger.LogWarning("The URL provided has SAS token. The resolution strategy won't be applied. Host: {Host}", uri.Host);

                return uri;
            }

            return await GetStorageUriWithSasTokenAsync(sourceUrl, blobSasPermissions);
        }

        private async Task<Uri> GetStorageUriWithSasTokenAsync(string sourceUrl, BlobSasPermissions permissions)
        {
            try
            {
                var blobUrl = new BlobUriBuilder(new Uri(sourceUrl));
                var blobServiceClient = blobServiceClientFactory(new Uri($"https://{blobUrl.Host}"));

                var userKey = await GetUserDelegationKeyAsync(blobServiceClient, blobUrl.AccountName);

                var sasBuilder = new BlobSasBuilder()
                {
                    BlobContainerName = blobUrl.BlobContainerName,
                    Resource = "c",
                    ExpiresOn = DateTimeOffset.UtcNow.AddHours(BlobSasTokenExpirationInHours)
                };

                sasBuilder.SetPermissions(permissions);

                var blobUriWithSas = new BlobUriBuilder(blobUrl.ToUri())
                {
                    Sas = sasBuilder.ToSasQueryParameters(userKey, blobUrl.AccountName)
                };

                return blobUriWithSas.ToUri();
            }
            catch (Exception e)
            {
                logger.LogError(e, "Error while creating SAS token for blob storage");
                throw;
            }
        }

        private async Task<UserDelegationKey> GetUserDelegationKeyAsync(BlobServiceClient blobServiceClient, string storageAccountName)
        {
            try
            {
                await semaphoreSlim.WaitAsync();

                var userDelegationKey = userDelegationKeyDictionary.GetValueOrDefault(storageAccountName);

                // https://www.allenconway.net/2023/11/dealing-with-time-skew-and-sas-azure.html
                // https://learn.microsoft.com/en-us/azure/storage/common/storage-sas-overview?toc=%2Fazure%2Fstorage%2Fblobs%2Ftoc.json&bc=%2Fazure%2Fstorage%2Fblobs%2Fbreadcrumb%2Ftoc.json#best-practices-when-using-sas paragraph referencing "clock skew"
                var now = DateTimeOffset.UtcNow.Subtract(TimeSpan.FromMinutes(15));

                if (userDelegationKey is null || userDelegationKey.SignedExpiresOn < now)
                {
                    userDelegationKey = await blobServiceClient.GetUserDelegationKeyAsync(startsOn: now, expiresOn: DateTimeOffset.UtcNow.AddHours(UserDelegationKeyExpirationInHours));

                    userDelegationKeyDictionary[storageAccountName] = userDelegationKey;
                }

                return userDelegationKey;
            }
            catch (Exception e)
            {
                logger.LogError(e, $"Error while getting user delegation key.");
                throw;
            }
            finally
            {
                semaphoreSlim.Release();
            }
        }

        private bool IsValidAzureStorageAccountUri(string uri)
        {
            return Uri.TryCreate(uri, UriKind.Absolute, out var result) &&
                   result.Scheme == "https" &&
                   result.Host.EndsWith(storageHostSuffix);
        }
    }
}
