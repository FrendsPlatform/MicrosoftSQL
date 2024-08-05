﻿using Frends.MicrosoftSQL.BulkInsert.Definitions;
using Newtonsoft.Json.Linq;
using System;
using System.ComponentModel;
using System.Data;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;

namespace Frends.MicrosoftSQL.BulkInsert;

/// <summary>
/// MicrosoftSQL Task.
/// </summary>
public class MicrosoftSQL
{
    /// Mem cleanup.
    static MicrosoftSQL()
    {
        var currentAssembly = Assembly.GetExecutingAssembly();
        var currentContext = AssemblyLoadContext.GetLoadContext(currentAssembly);
        if (currentContext != null)
            currentContext.Unloading += OnPluginUnloadingRequested;
    }

    /// <summary>
    /// Execute bulk insert JSON data to Microsoft SQL Server.
    /// [Documentation](https://tasks.frends.com/tasks/frends-tasks/Frends.MicrosoftSQL.BulkInsert)
    /// </summary>
    /// <param name="input">Input parameters</param>
    /// <param name="options">Optional parameters</param>
    /// <param name="cancellationToken">Token generated by Frends to stop this Task.</param>
    /// <returns>Object { bool Success, long Count, string ErrorMessage }</returns>
    public static async Task<Result> BulkInsert([PropertyTab] Input input, [PropertyTab] Options options, CancellationToken cancellationToken)
    {
        var inputJson = @"{""data"": {""Table"": " + input.InputData + @"
              }
            }";

        try
        {
            DataSet dataSet = JObject.Parse(inputJson)["data"].ToObject<DataSet>();
            _ = dataSet.Tables["Table"];

            using var connection = new SqlConnection(input.ConnectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            if (options.ConvertEmptyPropertyValuesToNull)
                // convert string.Empty values to null (this allows inserting data to fields which are different than text (int, ..)
                SetEmptyDataRowsToNull(dataSet);

            if (options.SqlTransactionIsolationLevel is SqlTransactionIsolationLevel.None)
            {
                try
                {
                    var result = await ExecuteHandler(options, input.TableName, dataSet, new SqlBulkCopy(connection, GetSqlBulkCopyOptions(options), null), cancellationToken);
                    return new Result(true, result, null);
                }
                catch (Exception ex)
                {
                    if (options.ThrowErrorOnFailure)
                        throw new Exception("BulkInsert exception: 'Options.SqlTransactionIsolationLevel = None', so there was no transaction rollback.", ex);
                    else
                        return new Result(false, 0, $"ExecuteHandler exception: 'Options.SqlTransactionIsolationLevel = None', so there was no transaction rollback. {ex}");
                }
            }


            using var transaction = connection.BeginTransaction(GetIsolationLevel(options));

            try
            {
                var result = await ExecuteHandler(options, input.TableName, dataSet, new SqlBulkCopy(connection, GetSqlBulkCopyOptions(options), transaction), cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return new Result(true, result, null);
            }
            catch (Exception ex)
            {
                try
                {
                    await transaction.RollbackAsync(cancellationToken);
                }
                catch (Exception rollbackEx)
                {
                    if (options.ThrowErrorOnFailure)
                        throw new Exception("BulkInsert exception: An exception occurred on transaction rollback.", rollbackEx);
                    else
                        return new Result(false, 0, $"BulkInsert exception: An exception occurred on transaction rollback. Rollback exception: {rollbackEx}. ||  Exception leading to rollback: {ex}");
                }

                if (options.ThrowErrorOnFailure)
                    throw new Exception("BulkInsert exception: (If required) transaction rollback completed without exception.", ex);
                else
                    return new Result(false, 0, $"BulkInsert exception: (If required) transaction rollback completed without exception. {ex}.");
            }
        }
        catch (Exception e)
        {
            if (options.ThrowErrorOnFailure)
                throw new Exception("BulkInsert exception: ", e);
            else
                return new Result(false, 0, $"BulkInsert exception: {e}");
        }
        finally
        {
            SqlConnection.ClearAllPools();
        }
    }

    private static async Task<long> ExecuteHandler(Options options, string tableName, DataSet dataSet, SqlBulkCopy sqlBulkCopy, CancellationToken cancellationToken)
    {
        var rowsCopied = 0L;
        try
        {
            using (sqlBulkCopy)
            {
                sqlBulkCopy.BulkCopyTimeout = options.CommandTimeoutSeconds;
                sqlBulkCopy.DestinationTableName = tableName;
                sqlBulkCopy.SqlRowsCopied += (s, e) => rowsCopied = e.RowsCopied;
                sqlBulkCopy.NotifyAfter = options.NotifyAfter;

                await sqlBulkCopy.WriteToServerAsync(dataSet.Tables[0], cancellationToken).ConfigureAwait(false);

                return rowsCopied;
            }
        }
        catch (Exception ex)
        {
            var notifyRange = rowsCopied + (options.NotifyAfter - 1);
            throw new Exception($"ExecuteHandler exception, procecced row count between: {rowsCopied} and {notifyRange} (see Options.NotifyAfter). {ex}");
        }
    }

    private static void SetEmptyDataRowsToNull(DataSet dataSet)
    {
        foreach (var table in dataSet.Tables.Cast<DataTable>())
            foreach (var row in table.Rows.Cast<DataRow>())
                foreach (var column in row.ItemArray)
                    if (column.ToString() == string.Empty)
                    {
                        var index = Array.IndexOf(row.ItemArray, column);
                        row[index] = null;
                    }
    }

    private static SqlBulkCopyOptions GetSqlBulkCopyOptions(Options options)
    {
        SqlBulkCopyOptions sqlBulkCopyOptions = (int)SqlBulkCopyOptions.Default;

        if (options.FireTriggers)
            sqlBulkCopyOptions += (int)SqlBulkCopyOptions.FireTriggers;

        if (options.KeepIdentity)
            sqlBulkCopyOptions += (int)SqlBulkCopyOptions.KeepIdentity;

        if (options.TableLock)
            sqlBulkCopyOptions += (int)SqlBulkCopyOptions.TableLock;

        if (options.KeepNulls)
            sqlBulkCopyOptions += (int)SqlBulkCopyOptions.KeepNulls;

        return sqlBulkCopyOptions;
    }

    private static IsolationLevel GetIsolationLevel(Options options)
    {
        return options.SqlTransactionIsolationLevel switch
        {
            SqlTransactionIsolationLevel.Unspecified => IsolationLevel.Unspecified,
            SqlTransactionIsolationLevel.ReadUncommitted => IsolationLevel.ReadUncommitted,
            SqlTransactionIsolationLevel.ReadCommitted => IsolationLevel.ReadCommitted,
            SqlTransactionIsolationLevel.RepeatableRead => IsolationLevel.RepeatableRead,
            SqlTransactionIsolationLevel.Serializable => IsolationLevel.Serializable,
            SqlTransactionIsolationLevel.Snapshot => IsolationLevel.Snapshot,
            _ => IsolationLevel.ReadCommitted,
        };
    }

    private static void OnPluginUnloadingRequested(AssemblyLoadContext obj)
    {
        obj.Unloading -= OnPluginUnloadingRequested;
    }
}