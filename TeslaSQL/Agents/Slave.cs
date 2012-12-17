﻿#region Using Statements
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Data;
using System.Xml;
using Xunit;
using TeslaSQL.DataUtils;
using TeslaSQL.DataCopy;
#endregion

namespace TeslaSQL.Agents {

    class ChangeTable {
        public readonly string name;
        public readonly Int64? ctid;
        public readonly string slaveName;
        public readonly string schemaName;
        public string ctName {
            get {
                return string.Format("tblCT{1}_{2}", schemaName, name, ctid);
            }
        }
        public string consolidatedName {
            get {
                //return string.Format("[{0}].[tblCT{1}_{2}]", schemaName, name, slaveName);
                return string.Format("tblCT{1}_{2}", schemaName, name, slaveName);
            }
        }

        public ChangeTable(string name, Int64? ctid, String schema, string slaveName) {
            this.name = name;
            this.ctid = ctid;
            this.schemaName = schema;
            this.slaveName = slaveName;
        }
    }

    //TODO throughout this class add error handling for tables that shouldn't stop on error
    //TODO we need to set up field lists somewhere in here...
    //TODO figure out where to put check for MSSQL vs. netezza and where to branch the code paths
    public class Slave : Agent {

        public Slave(Config config, IDataUtils sourceDataUtils, IDataUtils destDataUtils) {
            this.config = config;
            this.sourceDataUtils = sourceDataUtils;
            this.destDataUtils = destDataUtils;
            //log server is source since source is relay for slave
            this.logger = new Logger(config.logLevel, config.statsdHost, config.statsdPort, config.errorLogDB, sourceDataUtils);
        }

        public Slave() {
            //this constructor is only used by for running unit tests
            this.logger = new Logger(LogLevel.Critical, null, null, null);
        }

        public override void ValidateConfig() {
            Config.ValidateRequiredHost(config.relayServer);
            Config.ValidateRequiredHost(config.slave);
            if (config.relayType == null || config.slaveType == null) {
                throw new Exception("Slave agent requires a valid SQL flavor for relay and slave");
            }
        }

        public override void Run() {
            logger.Log("Initializing CT batch", LogLevel.Trace);
            List<ChangeTrackingBatch> batches = InitializeBatch();

            /**
             * If you run a batch as Multi, and that batch fails, and before the next run,
             * you increase the batchConsolidationThreshold, this can lead to unexpected behaviour.
             */
            if (config.batchConsolidationThreshold == 0 || batches.Count < config.batchConsolidationThreshold) {
                foreach (var batch in batches) {
                    RunSingleBatch(batch);
                }
            } else {
                RunMultiBatch(batches);
            }

            logger.Log("Slave agent work complete", LogLevel.Info);
            return;
        }


        /// <summary>
        /// Initializes version/batch info for a run
        /// </summary>
        /// <returns>List of change tracking batches to work on</returns>
        private List<ChangeTrackingBatch> InitializeBatch() {
            var batches = new List<ChangeTrackingBatch>();
            ChangeTrackingBatch ctb;

            var incompleteBatches = sourceDataUtils.GetPendingCTSlaveVersions(config.relayDB);
            if (incompleteBatches.Rows.Count > 0) {
                foreach (DataRow row in incompleteBatches.Rows) {
                    batches.Add(new ChangeTrackingBatch(row));
                }
                return batches;
            }
            //get the last CT version this slave worked on in tblCTSlaveVersion
            logger.Log("Retrieving information on last run for slave " + config.slave, LogLevel.Debug);

            DataRow lastBatch = sourceDataUtils.GetLastCTBatch(config.relayDB, AgentType.Slave, config.slave);
            if (lastBatch == null) {
                ctb = new ChangeTrackingBatch(1, 0, 0, 0);
                batches.Add(ctb);
                return batches;
            }

            //compare bitwise to the bit for last step of slave agent
            if ((lastBatch.Field<Int32>("syncBitWise") & Convert.ToInt32(SyncBitWise.SyncHistoryTables)) > 0) {
                logger.Log("Last batch was successful, checking for new batches.", LogLevel.Debug);

                DataTable pendingVersions = sourceDataUtils.GetPendingCTVersions(config.relayDB, lastBatch.Field<Int64>("CTID"), Convert.ToInt32(SyncBitWise.UploadChanges));
                logger.Log("Retrieved " + pendingVersions.Rows.Count + " pending CT version(s) to work on.", LogLevel.Debug);

                foreach (DataRow row in pendingVersions.Rows) {
                    ctb = new ChangeTrackingBatch(row);
                    batches.Add(ctb);
                    sourceDataUtils.CreateSlaveCTVersion(config.relayDB, ctb, config.slave);
                }
                return batches;
            }
            ctb = new ChangeTrackingBatch(lastBatch);
            logger.Log("Last batch failed, retrying CTID " + ctb.CTID, LogLevel.Warn);
            batches.Add(ctb);
            return batches;
        }


        /// <summary>
        /// Consolidates multiple batches and runs them as a group
        /// </summary>
        /// <param name="ctidTable">DataTable object listing all the batches</param>
        private void RunMultiBatch(List<ChangeTrackingBatch> batches) {
            //TODO add logger statements
            var existingCTTables = new List<ChangeTable>();

            //get last batch in hte list
            ChangeTrackingBatch endBatch = batches.OrderBy(item => item.CTID).Last();

            //from here forward all operations will use the bitwise value for the last CTID since they are operating on this whole set of batches

            foreach (ChangeTrackingBatch batch in batches) {
                logger.Log("Populating list of changetables for CTID : " + batch.CTID, LogLevel.Debug);
                existingCTTables = existingCTTables.Concat(PopulateTableList(config.tables, config.slaveCTDB, batch.CTID)).ToList();
            }
            SetFieldLists(config.slaveDB, config.tables, destDataUtils);
            if ((endBatch.syncBitWise & Convert.ToInt32(SyncBitWise.ConsolidateBatches)) == 0) {
                logger.Log("Consolidating batches", LogLevel.Trace);
                ConsolidateBatches(existingCTTables, batches);
                sourceDataUtils.WriteBitWise(config.relayDB, endBatch.CTID, Convert.ToInt32(SyncBitWise.ConsolidateBatches), AgentType.Slave);
            }

            if ((endBatch.syncBitWise & Convert.ToInt32(SyncBitWise.DownloadChanges)) == 0) {
                logger.Log("Downloading consolidated changetables", LogLevel.Debug);
                CopyChangeTables(config.tables, config.relayDB, config.slaveCTDB, endBatch.CTID, isConsolidated: true);
                logger.Log("Changes downloaded successfully", LogLevel.Debug);
                sourceDataUtils.WriteBitWise(config.relayDB, endBatch.CTID, Convert.ToInt32(SyncBitWise.DownloadChanges), AgentType.Slave);
            }

            foreach (ChangeTrackingBatch batch in batches) {
                if ((batch.syncBitWise & Convert.ToInt32(SyncBitWise.ApplySchemaChanges)) == 0) {
                    logger.Log("Applying schema changes", LogLevel.Debug);
                    ApplySchemaChanges(config.tables, config.slaveDB, batch.CTID);
                    sourceDataUtils.WriteBitWise(config.relayDB, batch.CTID, Convert.ToInt32(SyncBitWise.ApplySchemaChanges), AgentType.Slave);
                }
            }

            if ((endBatch.syncBitWise & Convert.ToInt32(SyncBitWise.ApplyChanges)) == 0) {
                ApplyChanges(config.tables, config.slaveCTDB, existingCTTables, endBatch.CTID);
                sourceDataUtils.WriteBitWise(config.relayDB, endBatch.CTID, Convert.ToInt32(SyncBitWise.ApplyChanges), AgentType.Slave);
            }

            //SyncBatchedHistoryTables(config.tables, config.slaveCTDB, config.slaveDB, tables);
            //success! go through and mark all the batches as complete in the db
            foreach (ChangeTrackingBatch batch in batches) {
                sourceDataUtils.MarkBatchComplete(config.relayDB, batch.CTID, Convert.ToInt32(SyncBitWise.SyncHistoryTables), DateTime.Now, AgentType.Slave, config.slave);
            }
        }

        private void ConsolidateBatches(List<ChangeTable> tables, List<ChangeTrackingBatch> batches) {
            IDataCopy dataCopy = DataCopyFactory.GetInstance(config.relayType.Value, config.slaveType.Value, sourceDataUtils, sourceDataUtils);
            var lu = new Dictionary<string, List<ChangeTable>>();
            foreach (var changeTable in tables) {
                if (!lu.ContainsKey(changeTable.name)) {
                    lu[changeTable.name] = new List<ChangeTable>();
                }
                lu[changeTable.name].Add(changeTable);
            }
            foreach (var table in config.tables) {
                if (!lu.ContainsKey(table.Name)) {
                    continue;
                }
                var lastChangeTable = lu[table.Name].OrderByDescending(c => c.ctid).First();
                dataCopy.CopyTable(config.relayDB, lastChangeTable.ctName, table.schemaName, config.relayDB, 36000, lastChangeTable.consolidatedName);
                dataCopy.CopyTableDefinition(config.relayDB, lastChangeTable.ctName, table.schemaName, config.relayDB, lastChangeTable.consolidatedName);
                foreach (var changeTable in lu[lastChangeTable.name].OrderByDescending(c => c.ctid)) {
                    sourceDataUtils.Consolidate(changeTable.ctName, changeTable.consolidatedName, config.relayDB, table.schemaName);
                }
                sourceDataUtils.RemoveDuplicatePrimaryKeyChangeRows(table, lastChangeTable.consolidatedName, config.relayDB);
            }
        }

        /// <summary>
        /// Runs a single change tracking batch
        /// </summary>
        /// <param name="CTID">Change tracking batch object to work on</param>
        private void RunSingleBatch(ChangeTrackingBatch ctb) {
            var existingCTTables = new List<ChangeTable>();

            if ((ctb.syncBitWise & Convert.ToInt32(SyncBitWise.DownloadChanges)) == 0) {
                existingCTTables = CopyChangeTables(config.tables, config.relayDB, config.slaveCTDB, ctb.CTID);
                sourceDataUtils.WriteBitWise(config.relayDB, ctb.CTID, Convert.ToInt32(SyncBitWise.DownloadChanges), AgentType.Slave);
                //marking this field so that completed slave batches will have the same values
                sourceDataUtils.WriteBitWise(config.relayDB, ctb.CTID, Convert.ToInt32(SyncBitWise.ConsolidateBatches), AgentType.Slave);
            } else {
                //since CopyChangeTables doesn't need to be called to fill in CT table list, get it from the slave instead
                existingCTTables = PopulateTableList(config.tables, config.slaveCTDB, ctb.CTID);
            }

            //apply schema changes if not already done
            if ((ctb.syncBitWise & Convert.ToInt32(SyncBitWise.ApplySchemaChanges)) == 0) {
                ApplySchemaChanges(config.tables, config.slaveDB, ctb.CTID);
                sourceDataUtils.WriteBitWise(config.relayDB, ctb.CTID, Convert.ToInt32(SyncBitWise.ApplySchemaChanges), AgentType.Slave);
                //marking this field so that all completed slave batches will have the same values
                sourceDataUtils.WriteBitWise(config.relayDB, ctb.CTID, Convert.ToInt32(SyncBitWise.ConsolidateBatches), AgentType.Slave);
            }
            //apply changes to destination tables if not already done
            if ((ctb.syncBitWise & Convert.ToInt32(SyncBitWise.ApplyChanges)) == 0) {
                ApplyChanges(config.tables, config.slaveDB, existingCTTables, ctb.CTID);
                sourceDataUtils.WriteBitWise(config.relayDB, ctb.CTID, Convert.ToInt32(SyncBitWise.ApplyChanges), AgentType.Slave);
            }

            //update the history tables
            //TODO implement
            //SyncBatchedHistoryTables(config.tables, config.slaveCTDB, config.slaveDB, tables);

            //success! mark the batch as complete
            sourceDataUtils.MarkBatchComplete(config.relayDB, ctb.CTID, Convert.ToInt32(SyncBitWise.SyncHistoryTables), DateTime.Now, AgentType.Slave, config.slave);
        }

        private void ApplyChanges(TableConf[] tableConf, string slaveDB, List<ChangeTable> tables, Int64 CTID) {
            SetFieldLists(slaveDB, tableConf, destDataUtils);
            var hasArchive = new Dictionary<TableConf, TableConf>();
            foreach (var table in tableConf) {
                if (tables.Any(s => s.name == table.Name)) {
                    if (hasArchive.ContainsKey(table)) {
                        //so we don't grab tblOrderArchive, insert tlbOrder: tblOrderArchive, and then go back and insert tblOrder: null.
                        continue;
                    }
                    if (table.Name.EndsWith("Archive")) {
                        string nonArchiveTableName = CTTableName(table.Name.Substring(0, table.Name.Length - table.Name.LastIndexOf("Archive")), CTID);
                        if (tables.Any(s => s.name == nonArchiveTableName)) {
                            var nonArchiveTable = tableConf.First(t => t.Name == nonArchiveTableName);
                            hasArchive[nonArchiveTable] = table;
                        } else {
                            hasArchive[table] = null;
                        }
                    } else {
                        hasArchive[table] = null;
                    }
                }
            }
            foreach (var tableArchive in hasArchive) {
                destDataUtils.ApplyTableChanges(tableArchive.Key, tableArchive.Value, config.slaveDB, CTID, config.slaveCTDB);
            }
        }


        /// <summary>
        /// For the specified list of tables, populate a list of which CT tables exist
        /// </summary>
        /// <param name="tables">Array of table config objects</param>
        /// <param name="dbName">Database name</param>
        /// <param name="tables">List of table names to populate</param>
        private List<ChangeTable> PopulateTableList(TableConf[] tables, string dbName, Int64 CTID) {
            //TODO add logger statements
            var tableList = new List<ChangeTable>();
            foreach (TableConf t in tables) {
                var ct = new ChangeTable(t.Name, CTID, t.schemaName, config.slave);
                if (sourceDataUtils.CheckTableExists(dbName, ct.ctName, t.schemaName)) {
                    tableList.Add(ct);
                } else {
                    logger.Log("Did not find table " + ct.ctName, LogLevel.Debug);
                }
            }
            return tableList;
        }

        /// <summary>
        /// Given a table name and CTID, returns the CT table name
        /// </summary>
        /// <param name="table">Table name</param>
        /// <param name="CTID">Change tracking batch iD</param>
        /// <returns>CT table name</returns>
        public string CTTableName(string table, Int64? CTID = null) {
            if (CTID != null) {
                return "tblCT" + table + "_" + Convert.ToString(CTID);
            } else {
                //consolidated table always has the same name
                return "tblCT" + table + "_" + config.slave;
            }
        }


        /// <summary>
        /// Copies change tables from the master to the relay server
        /// </summary>
        /// <param name="tables">Array of table config objects</param>
        /// <param name="sourceCTDB">Source CT database</param>
        /// <param name="destCTDB">Dest CT database</param>
        /// <param name="CTID">CT batch ID this is for</param>
        /// <param name="tables">Reference variable, list of tables that have >0 changes. Passed by ref instead of output
        ///     because in multi batch mode it is built up over several calls to this method.</param>
        private List<ChangeTable> CopyChangeTables(TableConf[] tables, string sourceCTDB, string destCTDB, Int64 CTID, bool isConsolidated = false) {
            bool found = false;
            var tableList = new List<ChangeTable>();
            IDataCopy dataCopy = DataCopyFactory.GetInstance(config.relayType.Value, config.slaveType.Value, sourceDataUtils, destDataUtils);
            foreach (TableConf t in tables) {
                found = false;
                var ct = new ChangeTable(t.Name, CTID, t.schemaName, config.slave);
                string sourceCTTable = isConsolidated ? ct.consolidatedName : ct.ctName;
                string destCTTable = ct.ctName;
                //attempt to copy the change table locally
                try {
                    //hard coding timeout at 1 hour for bulk copy
                    dataCopy.CopyTable(sourceCTDB, sourceCTTable, t.schemaName, destCTDB, 36000, destCTTable);
                    logger.Log("Copied table " + t.schemaName + "." + sourceCTTable + " to slave", LogLevel.Trace);
                    found = true;
                } catch (DoesNotExistException) {
                    //this is a totally normal and expected case since we only publish changetables when data actually changed
                    logger.Log("No changes to pull for table " + t.schemaName + "." + sourceCTTable + " because it does not exist ", LogLevel.Debug);
                } catch (Exception e) {
                    if (t.stopOnError) {
                        throw;
                    } else {
                        logger.Log("Copying change data for table " + t.schemaName + "." + sourceCTTable + " failed with error: " + e.Message, LogLevel.Error);
                    }
                }
                if (found) {
                    tableList.Add(ct);
                }
            }
            return tableList;
        }

        public void ApplySchemaChanges(TableConf[] tables, string destDB, Int64 CTID) {
            //get list of schema changes from tblCTSChemaChange_ctid on the relay server/db
            DataTable result = sourceDataUtils.GetSchemaChanges(config.relayDB, CTID);

            if (result == null) {
                return;
            }

            TableConf t;
            foreach (DataRow row in result.Rows) {
                var schemaChange = new SchemaChange(row);
                //String.Compare method returns 0 if the strings are equal, the third "true" flag is for a case insensitive comparison
                t = tables.SingleOrDefault(item => String.Compare(item.Name, schemaChange.tableName, ignoreCase: true) == 0);

                if (t == null) {
                    logger.Log("Ignoring schema change for table " + row.Field<string>("CscTableName") + " because it isn't in config", LogLevel.Debug);
                    continue;
                }
                logger.Log("Processing schema change (CscID: " + row.Field<int>("CscID") +
                    ") of type " + schemaChange.eventType + " for table " + t.Name, LogLevel.Info);

                if (t.columnList == null || t.columnList.Contains(schemaChange.columnName, StringComparer.OrdinalIgnoreCase)) {
                    logger.Log("Schema change applies to a valid column, so we will apply it", LogLevel.Info);
                    switch (schemaChange.eventType) {
                        case SchemaChangeType.Rename:
                            logger.Log("Renaming column " + schemaChange.columnName + " to " + schemaChange.newColumnName, LogLevel.Info);
                            destDataUtils.RenameColumn(t, destDB, schemaChange.schemaName, schemaChange.tableName,
                                schemaChange.columnName, schemaChange.newColumnName);
                            break;
                        case SchemaChangeType.Modify:
                            logger.Log("Changing data type on column " + schemaChange.columnName, LogLevel.Info);
                            destDataUtils.ModifyColumn(t, destDB, schemaChange.schemaName, schemaChange.tableName, schemaChange.columnName, schemaChange.dataType.ToString());
                            break;
                        case SchemaChangeType.Add:
                            logger.Log("Adding column " + schemaChange.columnName, LogLevel.Info);
                            destDataUtils.AddColumn(t, destDB, schemaChange.schemaName, schemaChange.tableName, schemaChange.columnName, schemaChange.dataType.ToString());
                            break;
                        case SchemaChangeType.Drop:
                            logger.Log("Dropping column " + schemaChange.columnName, LogLevel.Info);
                            destDataUtils.DropColumn(t, destDB, schemaChange.schemaName, schemaChange.tableName, schemaChange.columnName);
                            break;
                    }

                } else {
                    logger.Log("Skipped schema change because the column it impacts is not in our list", LogLevel.Info);
                }

            }
        }
    }
}
