﻿// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Polly;

namespace Tes.Repository
{
    /// <summary>
    /// A repository for storing <typeparamref name="T"/> in an Entity Framework Postgres table
    /// </summary>
    /// <typeparam name="T">Database table schema class</typeparam>
    public abstract class PostgreSqlCachingRepository<T> : IDisposable where T : class
    {
        private const int BatchSize = 1000;
        private static readonly TimeSpan defaultCompletedTaskCacheExpiration = TimeSpan.FromDays(1);

        protected readonly AsyncPolicy asyncPolicy = Policy
            .Handle<Npgsql.NpgsqlException>(e => e.IsTransient)
            .WaitAndRetryAsync(10, i => TimeSpan.FromSeconds(Math.Pow(2, i)));

        private record struct WriteItem(T DbItem, WriteAction Action, TaskCompletionSource<T> TaskSource);
        private readonly Channel<WriteItem> itemsToWrite = Channel.CreateUnbounded<WriteItem>();
        private readonly ConcurrentDictionary<T, object> updatingItems = new(); // Collection of all pending updates to be written, to faciliate detection of simultaneous parallel updates.
        private readonly CancellationTokenSource writerWorkerCancellationTokenSource = new();
        private readonly Task writerWorkerTask;

        protected enum WriteAction { Add, Update, Delete }

        protected Func<TesDbContext> CreateDbContext { get; init; }
        protected readonly ICache<T> Cache;
        protected readonly ILogger Logger;

        private bool _disposedValue;

        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="hostApplicationLifetime">Used for requesting termination of the current application if the writer task unexpectedly exits.</param>
        /// <param name="logger">Logging interface.</param>
        /// <param name="cache">Memory cache for fast access to active items.</param>
        /// <exception cref="System.Diagnostics.UnreachableException"></exception>
        protected PostgreSqlCachingRepository(Microsoft.Extensions.Hosting.IHostApplicationLifetime hostApplicationLifetime, ILogger logger = default, ICache<T> cache = default)
        {
            Logger = logger;
            Cache = cache;

            // The only "normal" exit for _writerWorkerTask is "cancelled". Anything else should force the process to exit because it means that this repository will no longer write to the database!
            writerWorkerTask = Task.Run(() => WriterWorkerAsync(writerWorkerCancellationTokenSource.Token))
                .ContinueWith(async task =>
                {
                    Logger?.LogInformation("The repository WriterWorkerAsync ended with TaskStatus: {TaskStatus}", task.Status);

                    if (task.Status == TaskStatus.Faulted)
                    {
                        Logger?.LogCritical(task.Exception, "Repository WriterWorkerAsync failed unexpectedly with: {ErrorMessage}.", task.Exception.Message);
                        Console.WriteLine($"Repository WriterWorkerAsync failed unexpectedly with: {task.Exception.Message}.");
                    }

                    const string errMessage = "Repository WriterWorkerAsync unexpectedly completed. The service will now be stopped.";
                    Logger?.LogCritical(errMessage);
                    Console.WriteLine(errMessage);

                    await Task.Delay(TimeSpan.FromSeconds(40)); // Give the logger time to flush; default flush is 30s
                    hostApplicationLifetime?.StopApplication();
                }, TaskContinuationOptions.NotOnCanceled)
                .ContinueWith(task => Logger?.LogInformation("The repository WriterWorkerAsync ended normally"), TaskContinuationOptions.OnlyOnCanceled);
        }

        /// <summary>
        /// Adds item to cache if active and not already present or updates it if already present.
        /// </summary>
        /// <param name="item"><see cref="T"/> to add or update to the cache.</param>
        /// <param name="GetKey">Function to provide cache key from <see cref="T"/>.</param>
        /// <param name="IsActive">Predicate to determine if <see cref="T"/> is active.</param>
        /// <param name="GetResult">Converts (extracts and/or copies) the desired portion of <typeparamref name="T"/>.</param>
        /// <returns><paramref name="item"/> (for convenience in fluent/LINQ usage patterns).</returns>
        protected TResult EnsureActiveItemInCache<TResult>(T item, Func<T, string> GetKey, Predicate<T> IsActive, Func<T, TResult> GetResult = default) where TResult : class
        {
            if (Cache is not null)
            {
                if (Cache.TryGetValue(GetKey(item), out _))
                {
                    _ = Cache.TryUpdate(GetKey(item), item, IsActive(item) ? default : defaultCompletedTaskCacheExpiration);
                }
                else if (IsActive(item))
                {
                    _ = Cache.TryAdd(GetKey(item), item);
                }
            }

            return GetResult?.Invoke(item) ?? default;
        }

        /// <summary>
        /// Retrieves items from the database in a consistent fashion.
        /// </summary>
        /// <param name="dbSet">The <see cref="DbSet{T}"/> of <typeparamref name="T"/> to query.</param>
        /// <param name="cancellationToken">A <see cref="CancellationToken"/> for controlling the lifetime of the asynchronous operation.</param>
        /// <param name="orderBy"><see cref="IQueryable{T}"/> order-by function.</param>
        /// <param name="pagination"><see cref="IQueryable{T}"/> pagination selection (within the order-by).</param>
        /// <param name="efPredicates">The WHERE clause parts <see cref="Expression"/> for <typeparamref name="T"/> selection in the query.</param>
        /// <param name="rawPredicate">The WHERE clause for raw SQL for <typeparamref name="T"/> selection in the query.</param>
        /// <returns></returns>
        /// <remarks>Ensure that the <see cref="DbContext"/> from which <paramref name="dbSet"/> comes isn't disposed until the entire query completes.</remarks>
        protected async Task<IEnumerable<T>> GetItemsAsync(DbSet<T> dbSet, CancellationToken cancellationToken, Func<IQueryable<T>, IQueryable<T>> orderBy = default, Func<IQueryable<T>, IQueryable<T>> pagination = default, IEnumerable<Expression<Func<T, bool>>> efPredicates = default, FormattableString rawPredicate = default)
        {
            ArgumentNullException.ThrowIfNull(dbSet);

            efPredicates = (efPredicates ??= []).ToList();

            orderBy ??= q => q;
            pagination ??= q => q;

            var tableQuery = rawPredicate is null
                ? dbSet.AsQueryable()
                : dbSet.FromSql(new PrependableFormattableString($"SELECT *\r\nFROM {dbSet.EntityType.GetTableName()}\r\nWHERE ", rawPredicate));

            tableQuery = efPredicates.Any()
                ? efPredicates.Aggregate(tableQuery, (query, efPredicate) => query.Where(efPredicate))
                : tableQuery;

            // Search for items in the JSON
            var query = pagination(orderBy(tableQuery));
            //var sqlQuery = query.ToQueryString();
            //System.Diagnostics.Debugger.Break();

            return await asyncPolicy.ExecuteAsync(query.ToListAsync, cancellationToken);
        }

        /// <summary>
        /// Adds entry into WriterWorker queue.
        /// </summary>
        /// <param name="item"></param>
        /// <param name="action"></param>
        /// <returns></returns>
        protected Task<T> AddUpdateOrRemoveItemInDbAsync(T item, WriteAction action, CancellationToken cancellationToken)
        {
            var source = new TaskCompletionSource<T>();
            var result = source.Task;

            if (action == WriteAction.Update)
            {
                if (updatingItems.TryAdd(item, null))
                {
                    result = source.Task.ContinueWith(RemoveUpdatingItem).Unwrap();
                }
                else
                {
                    throw new RepositoryCollisionException();
                }
            }

            if (!itemsToWrite.Writer.TryWrite(new(item, action, source)))
            {
                throw new InvalidOperationException("Failed to TryWrite to _itemsToWrite channel.");
            }

            return result;

            Task<T> RemoveUpdatingItem(Task<T> task)
            {
                _ = updatingItems.Remove(item, out _);
                return task.Status switch
                {
                    TaskStatus.RanToCompletion => Task.FromResult(task.Result),
                    TaskStatus.Faulted => Task.FromException<T>(task.Exception),
                    _ => Task.FromCanceled<T>(cancellationToken)
                };
            }
        }

        /// <summary>
        /// Continuously writes items to the database
        /// </summary>
        private async Task WriterWorkerAsync(CancellationToken cancellationToken)
        {
            var list = new List<WriteItem>();

            await foreach (var itemToWrite in itemsToWrite.Reader.ReadAllAsync(cancellationToken))
            {
                list.Add(itemToWrite);

                // Get remaining items up to _batchSize or until no more items are immediately available.
                while (list.Count < BatchSize && itemsToWrite.Reader.TryRead(out var additionalItem))
                {
                    list.Add(additionalItem);
                }

                await WriteItemsAsync(list, cancellationToken);
                list.Clear();
            }

            // If cancellation is requested, do not write any more items
        }

        private async ValueTask WriteItemsAsync(IList<WriteItem> dbItems, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (dbItems.Count == 0) { return; }

            using var dbContext = CreateDbContext();

            // Manually set entity state to avoid potential NPG PostgreSql bug
            dbContext.ChangeTracker.AutoDetectChangesEnabled = false;

            try
            {
                dbContext.AddRange(dbItems.Where(e => WriteAction.Add.Equals(e.Action)).Select(e => e.DbItem));
                dbContext.UpdateRange(dbItems.Where(e => WriteAction.Update.Equals(e.Action)).Select(e => e.DbItem));
                dbContext.RemoveRange(dbItems.Where(e => WriteAction.Delete.Equals(e.Action)).Select(e => e.DbItem));
                await asyncPolicy.ExecuteAsync(dbContext.SaveChangesAsync, cancellationToken);
                OperateOnAll(dbItems, ActionOnSuccess());
            }
            catch (Exception ex)
            {
                // It doesn't matter which item the failure was for, we will fail all items in this round.
                // TODO: are there exceptions Postgre or EF will send us that will tell us which item(s) failed or alternately succeeded?
                OperateOnAll(dbItems, ActionOnFailure(ex));
            }

            static void OperateOnAll(IEnumerable<WriteItem> sources, Action<WriteItem> action)
                => _ = Parallel.ForEach(sources, e => action(e));

            static Action<WriteItem> ActionOnFailure(Exception ex) =>
                e => _ = e.TaskSource.TrySetException(new AggregateException(Enumerable.Empty<Exception>().Append(ex)));

            static Action<WriteItem> ActionOnSuccess() =>
                e => _ = e.TaskSource.TrySetResult(e.DbItem);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposedValue)
            {
                if (disposing)
                {
                    writerWorkerCancellationTokenSource.Cancel();

                    try
                    {
                        writerWorkerTask.GetAwaiter().GetResult();
                    }
                    catch (AggregateException aex) when (aex?.InnerException is TaskCanceledException ex && writerWorkerCancellationTokenSource.Token == ex.CancellationToken)
                    { } // Expected return from Wait().
                    catch (TaskCanceledException ex) when (writerWorkerCancellationTokenSource.Token == ex.CancellationToken)
                    { } // Expected return from Wait().
                }

                _disposedValue = true;
            }
        }

        public void Dispose()
        {
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }

        internal class PrependableFormattableString : FormattableString
        {
            private readonly FormattableString source;
            private readonly string prefix;

            public PrependableFormattableString(string prefix, FormattableString formattableString)
            {
                ArgumentNullException.ThrowIfNull(formattableString);
                ArgumentException.ThrowIfNullOrEmpty(prefix);

                source = formattableString;
                this.prefix = prefix;
            }

            public override int ArgumentCount => source.ArgumentCount;

            public override string Format => prefix + source.Format;

            public override object GetArgument(int index)
            {
                return source.GetArgument(index);
            }

            public override object[] GetArguments()
            {
                return source.GetArguments();
            }

            public override string ToString(IFormatProvider formatProvider)
            {
                return prefix + source.ToString(formatProvider);
            }
        }
    }
}
