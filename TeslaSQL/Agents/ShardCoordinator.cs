﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using TeslaSQL.DataUtils;
using TeslaSQL.DataCopy;
using System.Threading;
using System.Threading.Tasks;

namespace TeslaSQL.Agents {
    /// <summary>
    /// This agent consolidates data from different shards so that slaves see a unified database
    /// </summary>
    public class ShardCoordinator : Agent {
        protected IEnumerable<string> shardDatabases;
        IList<TableConf> tablesWithChanges;
        Dictionary<TableConf, Dictionary<string, List<TColumn>>> tableDBFieldLists;
        public ShardCoordinator(IDataUtils dataUtils, Logger logger)
            : base(dataUtils, dataUtils, logger) {
            shardDatabases = Config.shardDatabases;
            tablesWithChanges = new List<TableConf>();
        }

        public ShardCoordinator() {
            //this constructor is only used by for running unit tests
            this.logger = new Logger(null, null, null, "");
        }

        public override void ValidateConfig() {
            Config.ValidateRequiredHost(Config.relayServer);
            if (Config.relayType == SqlFlavor.None) {
                throw new Exception("ShardCoordinator agent requires a valid SQL flavor for relay");
            }
            if (string.IsNullOrEmpty(Config.masterShard)) {
                throw new Exception("ShardCoordinator agent requires a master shard");
            }
            if (!Config.shardDatabases.Contains(Config.masterShard)) {
                throw new Exception("ShardCoordinator agent requires that the masterShard element be one of the shards listed in shardDatabases");
            }
        }

        public override void Run() {
            var batch = new ChangeTrackingBatch(sourceDataUtils.GetLastCTBatch(Config.relayDB, AgentType.ShardCoordinator));
            Logger.SetProperty("CTID", batch.CTID);
            if ((batch.syncBitWise & Convert.ToInt32(SyncBitWise.UploadChanges)) > 0) {
                CreateNewVersionsForShards(batch);
                return;
            }

            tableDBFieldLists = GetFieldListsByDB(batch.CTID);
            if (SchemasOutOfSync(tableDBFieldLists.Values)) {
                foreach (var sd in shardDatabases) {
                    sourceDataUtils.RevertCTBatch(sd, batch.CTID);
                }
                logger.Log("Schemas out of sync, quitting", LogLevel.Info);
                return;
            }
            if (AllShardMastersDone(batch)) {
                logger.Log("All shard masters are done; consolidating", LogLevel.Info);
                Consolidate(batch);
                sourceDataUtils.WriteBitWise(Config.relayDB, batch.CTID,
                    Convert.ToInt32(SyncBitWise.CaptureChanges) | Convert.ToInt32(SyncBitWise.UploadChanges), AgentType.ShardCoordinator);
            } else {
                logger.Log("Not all shards are done yet, waiting until they catch up", LogLevel.Info);
            }

        }

        private bool AllShardMastersDone(ChangeTrackingBatch batch) {
            return shardDatabases.All(dbName => (sourceDataUtils.GetCTBatch(dbName, batch.CTID).syncBitWise & Convert.ToInt32(SyncBitWise.UploadChanges)) > 0);
        }
        /// <param name="dbFieldLists">a list of maps from dbName to list of TColumns. 
        /// This is a list (not just a dict) because there needs to be one dict per table. </param>
        /// <returns></returns>
        // virtual so i can unit test it.
        virtual internal bool SchemasOutOfSync(IEnumerable<Dictionary<string, List<TColumn>>> dbFieldLists) {
            foreach (var dbFieldList in dbFieldLists) {
                var orderedFieldLists = dbFieldList.Values.Select(lc => lc.OrderBy(c => c.name));
                bool schemaOutOfSync = orderedFieldLists.Any(ofc => !ofc.SequenceEqual(orderedFieldLists.First()));
                if (schemaOutOfSync) {
                    return true;
                }
            }
            return false;
        }

        protected Dictionary<TableConf, Dictionary<string, List<TColumn>>> GetFieldListsByDB(Int64 ctid) {
            var fieldListByDB = new Dictionary<TableConf, Dictionary<string, List<TColumn>>>();
            foreach (var table in Config.tables) {
                var tDict = new Dictionary<string, List<TColumn>>();
                foreach (var sd in shardDatabases) {
                    //only add the columns if we get results. it's perfectly legitimate for a changetable to not exist for a given shard
                    //if it had no changes, and we don't want that to cause the schemas to be considered out of sync
                    var columns = sourceDataUtils.GetFieldList(sd, table.ToCTName(ctid), table.schemaName).Select(kvp => new TColumn(kvp.Key, kvp.Value)).ToList();
                    if (columns.Count > 0) {
                        tDict[sd] = columns;
                    }
                }
                fieldListByDB[table] = tDict;
            }
            return fieldListByDB;
        }

        private ChangeTrackingBatch CreateNewVersionsForShards(ChangeTrackingBatch batch) {
            logger.Log("Creating new CT versions for slaves", LogLevel.Info);
            Int64 ctid = sourceDataUtils.CreateCTVersion(Config.relayDB, 0, 0).CTID;
            Logger.SetProperty("CTID", ctid);
            foreach (var db in shardDatabases) {
                var b = new ChangeTrackingBatch(sourceDataUtils.GetLastCTBatch(db, AgentType.ShardCoordinator));
                sourceDataUtils.CreateShardCTVersion(db, ctid, b.syncStopVersion);
            }
            logger.Log("Created new CT Version " + ctid + " on " + string.Join(",", shardDatabases), LogLevel.Info);
            batch = new ChangeTrackingBatch(ctid, 0, 0, 0);
            return batch;
        }

        private void Consolidate(ChangeTrackingBatch batch) {
            if ((batch.syncBitWise & Convert.ToInt32(SyncBitWise.PublishSchemaChanges)) == 0) {
                logger.Log("Publishing schema changes", LogLevel.Debug);
                PublishSchemaChanges(batch);
                sourceDataUtils.WriteBitWise(Config.relayDB, batch.CTID, Convert.ToInt32(SyncBitWise.PublishSchemaChanges), AgentType.ShardCoordinator);
            }
            ConsolidateTables(batch);
            ConsolidateInfoTables(batch);
        }

        private void PublishSchemaChanges(ChangeTrackingBatch batch) {
            var dc = DataCopyFactory.GetInstance(Config.relayType, Config.relayType, sourceDataUtils, sourceDataUtils, logger);
            dc.CopyTable(Config.masterShard, batch.schemaChangeTable, "dbo", Config.relayDB, Config.dataCopyTimeout);
        }

        private void ConsolidateTables(ChangeTrackingBatch batch) {
            logger.Log("Consolidating tables", LogLevel.Info);
            var actions = new List<Action>();
            foreach (var tableDb in tableDBFieldLists) {
                var table = tableDb.Key;
                var firstDB = tableDb.Value.FirstOrDefault(t => t.Value.Count > 0).Key;
                if (firstDB == null) {
                    logger.Log("No shard has CT changes for table " + table.name, LogLevel.Debug);
                    continue;
                }
                tablesWithChanges.Add(table);
                SetFieldList(table, firstDB, batch);

                Action act = () => MergeTable(batch, tableDb.Value, table, firstDB);
                actions.Add(act);
            }
            logger.Log("Parallel invocation of " + actions.Count + " table merges", LogLevel.Trace);
            //interestingly, Parallel.Invoke does in fact bubble up exceptions, but not until after all threads have completed.
            //actually it looks like what it does is wrap its exceptions in an AggregateException. We don't ever catch those
            //though because if any exceptions happen inside of MergeTable it would generally be due to things like the server
            //being down or a query timeout.
            var options = new ParallelOptions();
            options.MaxDegreeOfParallelism = Config.maxThreads;
            Parallel.Invoke(options, actions.ToArray());
        }

        private void MergeTable(ChangeTrackingBatch batch, Dictionary<string, List<TColumn>> dbColumns, TableConf table, string firstDB) {
            logger.Log(new { message = "Merging table", Table = table.name }, LogLevel.Debug);
            var dc = DataCopyFactory.GetInstance(Config.relayType, Config.relayType, sourceDataUtils, sourceDataUtils, logger);
            dc.CopyTableDefinition(firstDB, table.ToCTName(batch.CTID), table.schemaName, Config.relayDB, table.ToCTName(batch.CTID));
            foreach (var dbNameFields in dbColumns) {
                var dbName = dbNameFields.Key;
                var columns = dbNameFields.Value;
                if (columns.Count == 0) {
                    //no changes in this DB for this table
                    continue;
                }
                sourceDataUtils.MergeCTTable(table, Config.relayDB, dbName, batch.CTID);
            }
        }

        private void ConsolidateInfoTables(ChangeTrackingBatch batch) {
            logger.Log("Consolidating info tables", LogLevel.Debug);
            var rowCounts = GetRowCounts(Config.tables, Config.relayDB, batch.CTID);
            PublishTableInfo(tablesWithChanges, Config.relayDB, rowCounts, batch.CTID);
        }

        private void SetFieldList(TableConf table, string database, ChangeTrackingBatch batch) {
            var cols = sourceDataUtils.GetFieldList(database, table.ToCTName(batch.CTID), table.schemaName);
            var pks = sourceDataUtils.GetPrimaryKeysFromInfoTable(table, batch.CTID, database);
            foreach (var pk in pks) {
                cols[pk] = true;
            }
            SetFieldList(table, cols);
        }
    }
}
