﻿using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.Linq;
using EnumsToSql.Logging;

namespace EnumsToSql
{
    /// <summary>
    /// The top level class for the EnumsToSql library. Create an instance by calling <see cref="Create"/>.
    /// </summary>
    public class EnumsToSqlWriter
    {
        /// <summary>
        /// The default name of the attribute which marks enums for replication.
        /// </summary>
        public const string DEFAULT_ATTRIBUTE_NAME = "EnumSqlTable";

        /// <summary>
        /// The list of enums which will be replicated to SQL.
        /// </summary>
        public List<EnumInfo> Enums { get; }

        EnumsToSqlWriter(List<EnumInfo> enums)
        {
            Enums = enums;
        }

        /// <summary>
        /// Creates a new instance of <see cref="EnumsToSqlWriter"/> based on a list of assembly file names.
        /// </summary>
        /// <param name="assemblyFiles">The assembly file names to load (including relative or absolute paths).</param>
        /// <param name="logger">The stream to send logging information to.</param>
        /// <param name="attributeName">The name of the attribute which marks enums for replication.</param>
        public static EnumsToSqlWriter Create(IEnumerable<string> assemblyFiles, Logger logger, string attributeName = DEFAULT_ATTRIBUTE_NAME)
        {
            var enumInfos = EnumInfo.GetEnumsFromAssemblies(assemblyFiles, attributeName, logger);
            return new EnumsToSqlWriter(enumInfos);
        }

        /// <summary>
        /// Updates the enum tables for multiple databases.
        /// </summary>
        /// <param name="connectionStrings">An enumeration of SQL Server connection strings. Each must include a specific database to connect to.</param>
        /// <param name="deletionMode">Determines what to do when an enum value exists in the database, but no longer exists in code.</param>
        /// <param name="logger">The stream to send logging information to.</param>
        /// <param name="inParallel">If true, multiple databases may be updated in parallel (up to the number of CPUs).</param>
        public void UpdateDatabases(IEnumerable<string> connectionStrings, DeletionMode deletionMode, Logger logger, bool inParallel = true)
        {
            if (logger == null)
                throw new ArgumentNullException(nameof(logger));

            if (inParallel)
            {
                var parallelConnectionStrings = connectionStrings.AsParallel()
                    .WithDegreeOfParallelism(Environment.ProcessorCount)
                    .WithExecutionMode(ParallelExecutionMode.ForceParallelism);

                var exceptions = new List<Exception>();

                parallelConnectionStrings.ForAll(
                    connStr =>
                    {
                        using (var childLogger = logger.CreateChildLogger())
                        {
                            try
                            {
                                UpdateDatabase(connStr, deletionMode, childLogger);
                            }
                            catch (Exception ex)
                            {
                                lock (exceptions)
                                {
                                    exceptions.Add(ex);
                                }
                            }
                        }
                    });

                if (exceptions.Count > 0)
                {
                    var aggregate = new AggregateException(exceptions);
                    throw new EnumsToSqlException("One or more databases failed to update (see log)", aggregate, isLogged: true);
                }
            }
            else
            {
                foreach (var connStr in connectionStrings)
                {
                    UpdateDatabase(connStr, deletionMode, logger);
                }
            }
        }

        /// <summary>
        /// Updates the enum tables for a single database.
        /// </summary>
        /// <param name="connectionString">The SQL Server connection string. This must include a specific database to connect to.</param>
        /// <param name="deletionMode">Determines what to do when an enum value exists in the database, but no longer exists in code.</param>
        /// <param name="logger">The stream to send logging information to.</param>
        public void UpdateDatabase(string connectionString, DeletionMode deletionMode, Logger logger)
        {
            if (logger == null)
                throw new ArgumentNullException(nameof(logger));

            if (Enums.Count == 0)
                return;

            try
            {
                using (var conn = new SqlConnection(connectionString))
                {
                    conn.Open();
                    UpdateDatabase(conn, deletionMode, logger);
                }
            }
            catch (Exception ex)
            {
                logger.Exception(ex);
                throw new EnumsToSqlException("Unable to update database", ex, isLogged: true);
            }
        }

        /// <summary>
        /// Updates the enum tables for a single database.
        /// </summary>
        /// <param name="conn">An open SQL connection to the database you want to update.</param>
        /// <param name="deletionMode">Determines what to do when an enum value exists in the database, but no longer exists in code.</param>
        /// <param name="logger">The stream to send logging information to.</param>
        public void UpdateDatabase(SqlConnection conn, DeletionMode deletionMode, Logger logger)
        {
            if (logger == null)
                throw new ArgumentNullException(nameof(logger));

            if (Enums.Count == 0)
                return;

            using (logger.OpenBlock($"Updating database {conn.Database} on {conn.DataSource}"))
            {
                try
                {
                    foreach (var enumInfo in Enums)
                    {
                        var existingRows = SqlExecutor.GetTableRows(conn, enumInfo);
                        var updatePlan = TableUpdatePlan.Create(enumInfo, existingRows, deletionMode);
                        SqlExecutor.UpdateTable(conn, enumInfo, updatePlan, logger);
                    }
                }
                catch (Exception ex)
                {
                    logger.Exception(ex);
                    throw new EnumsToSqlException("Unable to update database", ex, isLogged: true);
                }
            }
        }
    }
}