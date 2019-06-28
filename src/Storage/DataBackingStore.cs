﻿using System;
using System.Collections.Generic;
using System.Linq;
using SenseNet.ContentRepository.Storage.Data;
using SenseNet.ContentRepository.Storage.Data.SqlClient;
using SenseNet.ContentRepository.Storage.Schema;
using System.IO;
using SenseNet.ContentRepository.Storage.Caching.Dependency;
using System.Diagnostics;
using SenseNet.ContentRepository.Storage.Security;
using System.Globalization;
using System.Threading;
using SenseNet.Configuration;
using SenseNet.ContentRepository.Search.Indexing;
using SenseNet.Diagnostics;
using SenseNet.ContentRepository.Storage.Search;
using SenseNet.Search;
using SenseNet.Search.Indexing;
using SenseNet.Security;
using SenseNet.Tools;
using BlobStorage = SenseNet.ContentRepository.Storage.Data.BlobStorage;

namespace SenseNet.ContentRepository.Storage
{
    public static class DataBackingStore
    {
        private static object _indexDocumentProvider_Sync = new object();
        private static IIndexDocumentProvider __indexDocumentProvider;
        private static IIndexDocumentProvider IndexDocumentProvider
        {
            get
            {
                if (__indexDocumentProvider == null)
                {
                    lock (_indexDocumentProvider_Sync)
                    {
                        if (__indexDocumentProvider == null)
                        {
                            var types = TypeResolver.GetTypesByInterface(typeof(IIndexDocumentProvider));
                            if (types.Length != 1)
                                throw new ApplicationException("More than one IIndexDocumentProvider");
                            __indexDocumentProvider = Activator.CreateInstance(types[0]) as IIndexDocumentProvider;
                        }
                    }
                }
                return __indexDocumentProvider;
            }
        }

        // ====================================================================== Get NodeHead

        internal static NodeHead GetNodeHead(int nodeId) //UNDONE:DB@@@@@@@@ DELETE
        {
            return DataStore.LoadNodeHeadAsync(nodeId).Result;
        }
        internal static NodeHead GetNodeHead(string path) //UNDONE:DB@@@@@@@@ DELETE
        {
            return DataStore.LoadNodeHeadAsync(path).Result;
        }
        internal static IEnumerable<NodeHead> GetNodeHeads(IEnumerable<int> idArray) //UNDONE:DB@@@@@@@@ DELETE
        {
            return DataStore.LoadNodeHeadsAsync(idArray).Result;
        }
        internal static NodeHead GetNodeHeadByVersionId(int versionId)
        {
            return DataStore.LoadNodeHeadByVersionIdAsync(versionId).Result;
        }

        internal static bool NodeExists(string path) //UNDONE:DB@@@@@@@@ DELETE
        {
            return DataStore.NodeExistsAsync(path).Result;
        }

        // ====================================================================== Get Versions

        internal static NodeHead.NodeVersion[] GetNodeVersions(int nodeId)
        {
            return DataStore.GetNodeVersionsAsync(nodeId).Result;
        }

        // ====================================================================== Get NodeData

        internal static NodeToken GetNodeData(NodeHead head, int versionId) //UNDONE:DB@@@@@@@@ DELETE
        {
            return DataStore.LoadNodeAsync(head, versionId).Result;
        }
        internal static NodeToken[] GetNodeData(NodeHead[] headArray, int[] versionIdArray) //UNDONE:DB@@@@@@@@ DELETE
        {
            return DataStore.LoadNodesAsync(headArray, versionIdArray).Result;
        }
        // when create new
        internal static NodeData CreateNewNodeData(Node parent, NodeType nodeType, ContentListType listType, int listId)
        {
            var listTypeId = listType == null ? 0 : listType.Id;
            var parentId = parent == null ? 0 : parent.Id;
            var userId = AccessProvider.Current.GetOriginalUser().Id;
            var now = DataStore.RoundDateTime(DateTime.UtcNow);
            var name = String.Concat(nodeType.Name, "-", now.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture));
            var path = (parent == null) ? "/" + name : RepositoryPath.Combine(parent.Path, name);
            var versionNumber = new VersionNumber(1, 0, VersionStatus.Approved);

            var privateData = new NodeData(nodeType, listType)
            {
                IsShared = false,
                SharedData = null,

                Id = 0,
                NodeTypeId = nodeType.Id,
                ContentListTypeId = listTypeId,
                ContentListId = listId,

                ParentId = parentId,
                Name = name,
                Path = path,
                Index = 0,
                IsDeleted = false,

                CreationDate = now,
                ModificationDate = now,
                CreatedById = userId,
                ModifiedById = userId,
                OwnerId = userId,

                VersionId = 0,
                Version = versionNumber,
                VersionCreationDate = now,
                VersionModificationDate = now,
                VersionCreatedById = userId,
                VersionModifiedById = userId,

                Locked = false,
                LockedById = 0,
                ETag = null,
                LockType = 0,
                LockTimeout = 0,
                LockDate = DataStore.DateTimeMinValue,
                LockToken = null,
                LastLockUpdate = DataStore.DateTimeMinValue,

                //TODO: IsSystem

                SavingState = default(ContentSavingState),
                ChangedData = null
            };
            privateData.VersionModificationDateChanged = false;
            privateData.VersionModifiedByIdChanged = false;
            privateData.ModificationDateChanged = false;
            privateData.ModifiedByIdChanged = false;
            return privateData;
        }

        internal static void CacheNodeData(NodeData nodeData, string cacheKey = null)
        {
            if (nodeData == null)
                throw new ArgumentNullException("nodeData");
            if (cacheKey == null)
                cacheKey = GenerateNodeDataVersionIdCacheKey(nodeData.VersionId);
            var dependency = CacheDependencyFactory.CreateNodeDataDependency(nodeData);
            Cache.Insert(cacheKey, nodeData, dependency);
        }

        public static void RemoveNodeDataFromCacheByVersionId(int versionId)
        {
            Cache.Remove(GenerateNodeDataVersionIdCacheKey(versionId));
        }

        // ====================================================================== 

        internal static object LoadBinaryProperty(int versionId, PropertyType propertyType)
        {
            return DataStore.LoadBinaryPropertyValueAsync(versionId, propertyType.Id).Result;
        }

        internal static Stream GetBinaryStream(int nodeId, int versionId, int propertyTypeId)
        {
            BinaryCacheEntity binaryCacheEntity = null;

            if (TransactionScope.IsActive)
            {
                binaryCacheEntity = BlobStorage.LoadBinaryCacheEntity(versionId, propertyTypeId);
                return new SnStream(binaryCacheEntity.Context, binaryCacheEntity.RawData);
            }

            // Try to load cached binary entity
            var cacheKey = BinaryCacheEntity.GetCacheKey(versionId, propertyTypeId);
            binaryCacheEntity = (BinaryCacheEntity)Cache.Get(cacheKey);
            if (binaryCacheEntity == null)
            {
                // Not in cache, load it from the database
                binaryCacheEntity = BlobStorage.LoadBinaryCacheEntity(versionId, propertyTypeId);

                // insert the binary cache entity into the 
                // cache only if we know the node id
                if (binaryCacheEntity != null && nodeId != 0)
                {
                    if (!RepositoryEnvironment.WorkingMode.Populating)
                    {
                        var head = NodeHead.Get(nodeId);
                        Cache.Insert(cacheKey, binaryCacheEntity,
                            CacheDependencyFactory.CreateBinaryDataDependency(nodeId, head.Path, head.NodeTypeId));
                    }
                }
            }

            // Not found even in the database
            if (binaryCacheEntity == null)
                return null;
            if (binaryCacheEntity.Length == -1)
                return null;
            return new SnStream(binaryCacheEntity.Context, binaryCacheEntity.RawData);
        }
        internal static Dictionary<int, string> LoadTextProperties(int versionId, int[] notLoadedPropertyTypeIds)
        {
            return DataStore.LoadTextPropertyValuesAsync(versionId, notLoadedPropertyTypeIds).Result;
        }

        // ====================================================================== Transaction callback

        internal static void RemoveFromCache(NodeDataParticipant participant) // orig name: OnNodeDataCommit
        {
            // Do not fire any events if the node is new: 
            // it cannot effect any other content
            if (participant.IsNewNode)
                return;

            var data = participant.Data;

            // Remove items from Cache by the OriginalPath, before getting an update
            // of a - occassionally differring - path from the database
            if (data.PathChanged)
            {
                PathDependency.FireChanged(data.OriginalPath);
            }

            if (data.ContentListTypeId != 0 && data.ContentListId == 0)
            {
                // If list, invalidate full subtree
                PathDependency.FireChanged(data.Path);
            }
            else
            {
                // If not a list, invalidate item
                NodeIdDependency.FireChanged(data.Id);
            }
        }
        internal static void OnNodeDataRollback(NodeDataParticipant participant)
        {
            participant.Data.Rollback();
        }

        // ====================================================================== Cache Key factory

        private static readonly string NODE_HEAD_PREFIX = "NodeHeadCache.";
        private static readonly string NODE_DATA_PREFIX = "NodeData.";

        public static string CreateNodeHeadPathCacheKey(string path)
        {
            return string.Concat(NODE_HEAD_PREFIX, path.ToLowerInvariant());
        }
        internal static string GenerateNodeDataVersionIdCacheKey(int versionId)
        {
            return string.Concat(NODE_DATA_PREFIX, versionId);
        }

        // ====================================================================== Save Nodedata

        private const int maxDeadlockIterations = 3;
        private const int sleepIfDeadlock = 1000;

        internal static void SaveNodeData(Node node, NodeSaveSettings settings, IIndexPopulator populator, string originalPath, string newPath)
        {
            var isNewNode = node.Id == 0;
            var isOwnerChanged = node.Data.IsPropertyChanged("OwnerId");
            if (!isNewNode && isOwnerChanged)
                node.Security.Assert(PermissionType.TakeOwnership);

            var data = node.Data;
            var attempt = 0;

            using (var op = SnTrace.Database.StartOperation("SaveNodeData"))
            {
                while (true)
                {
                    attempt++;

                    var deadlockException = SaveNodeDataTransactional(node, settings, populator, originalPath, newPath);
                    if (deadlockException == null)
                        break;

                    SnTrace.Database.Write("DEADLOCK detected. Attempt: {0}/{1}, NodeId:{2}, Version:{3}, Path:{4}",
                        attempt, maxDeadlockIterations, node.Id, node.Version, node.Path);

                    if (attempt >= maxDeadlockIterations)
                        throw new Exception(string.Format("Error saving node. Id: {0}, Path: {1}", node.Id, node.Path), deadlockException);

                    SnLog.WriteWarning("Deadlock detected in SaveNodeData", properties:
                        new Dictionary<string, object>
                        {
                            {"Id: ", node.Id},
                            {"Path: ", node.Path},
                            {"Version: ", node.Version},
                            {"Attempt: ", attempt}
                        });

                    System.Threading.Thread.Sleep(sleepIfDeadlock);
                }
                op.Successful = true;
            }

            try
            {
                if (isNewNode)
                {
                    SecurityHandler.CreateSecurityEntity(node.Id, node.ParentId, node.OwnerId);
                }
                else if (isOwnerChanged)
                {
                    SecurityHandler.ModifyEntityOwner(node.Id, node.OwnerId);
                }
            }
            catch (EntityNotFoundException e)
            {
                SnLog.WriteException(e, $"Error during creating or modifying security entity: {node.Id}. Original message: {e}",
                    EventId.Security);
            }
            catch (SecurityStructureException) // suppressed
            {
                // no need to log this: somebody else already created or modified this security entity
            }

            if (isNewNode)
                SnTrace.ContentOperation.Write("Node created. Id:{0}, Path:{1}", data.Id, data.Path);
            else
                SnTrace.ContentOperation.Write("Node updated. Id:{0}, Path:{1}", data.Id, data.Path);
        }
        private static Exception SaveNodeDataTransactional(Node node, NodeSaveSettings settings, IIndexPopulator populator, string originalPath, string newPath)
        {
            IndexDocumentData indexDocument = null;
            bool hasBinary = false;

            var data = node.Data;
            var isNewNode = data.Id == 0;
            NodeDataParticipant participant = null;

            var msg = "Saving Node#" + node.Id + ", " + node.ParentPath + "/" + node.Name;
            var isLocalTransaction = !TransactionScope.IsActive;

            using (var op = SnTrace.Database.StartOperation(msg))
            {
                try
                {
                    // collect data for populator
                    var populatorData = populator.BeginPopulateNode(node, settings, originalPath, newPath);

                    data.CreateSnapshotData();

                    participant = new NodeDataParticipant { Data = data, Settings = settings, IsNewNode = isNewNode };

                    if (settings.NodeHead != null)
                    {
                        settings.LastMajorVersionIdBefore = settings.NodeHead.LastMajorVersionId;
                        settings.LastMinorVersionIdBefore = settings.NodeHead.LastMinorVersionId;
                    }

                    // Finalize path
                    string path;

                    if (data.Id != Identifiers.PortalRootId)
                    {
                        var parent = NodeHead.Get(data.ParentId);
                        if (parent == null)
                            throw new ContentNotFoundException(data.ParentId.ToString());
                        path = RepositoryPath.Combine(parent.Path, data.Name);
                    }
                    else
                    {
                        path = Identifiers.RootPath;
                    }
                    Node.AssertPath(path);
                    data.Path = path;

                    // Store in the database
                    int lastMajorVersionId, lastMinorVersionId;

                    var head = DataStore.SaveNodeAsync(data, settings, CancellationToken.None).Result;
                    lastMajorVersionId = settings.LastMajorVersionIdAfter;
                    lastMinorVersionId = settings.LastMinorVersionIdAfter;
                    node.RefreshVersionInfo(head);

                    // here we re-create the node head to insert it into the cache and refresh the version info);
                    if (lastMajorVersionId > 0 || lastMinorVersionId > 0)
                    {
                        if (!settings.DeletableVersionIds.Contains(node.VersionId))
                        {
                            // Elevation: we need to create the index document with full
                            // control to avoid field access errors (indexing must be independent
                            // from the current users permissions).
                            using (new SystemAccount())
                            {
                                indexDocument = SaveIndexDocument(node, true, isNewNode, out hasBinary);
                            }
                        }
                    }

                    // populate index only if it is enabled on this content (e.g. preview images will be skipped)
                    using (var op2 = SnTrace.Index.StartOperation("Indexing node"))
                    {
                        if (node.IsIndexingEnabled)
                        {
                            using (new SystemAccount())
                                populator.CommitPopulateNode(populatorData, indexDocument);
                        }

                        if (indexDocument != null && hasBinary)
                        {
                            using (new SystemAccount())
                            {
                                indexDocument = SaveIndexDocument(node, indexDocument);
                                populator.FinalizeTextExtracting(populatorData, indexDocument);
                            }
                        }
                        op2.Successful = true;
                    }
                }
                catch (System.Data.Common.DbException dbe)
                {
                    if (isLocalTransaction && IsDeadlockException(dbe))
                        return dbe;
                    throw SavingExceptionHelper(data, dbe);
                }
                catch (Exception e)
                {
                    var ee = SavingExceptionHelper(data, e);
                    if (ee == e)
                        throw;
                    else
                        throw ee;
                }
                op.Successful = true;
            }
            return null;
        }

        private static bool IsDeadlockException(System.Data.Common.DbException e)
        {
            // Avoid [SqlException (0x80131904): Transaction (Process ID ??) was deadlocked on lock resources with another process and has been chosen as the deadlock victim. Rerun the transaction.
            // CAUTION: Using e.ErrorCode and testing for HRESULT 0x80131904 will not work! you should use e.Number not e.ErrorCode
            var sqlEx = e as System.Data.SqlClient.SqlException;
            if (sqlEx == null)
                return false;
            var sqlExNumber = sqlEx.Number;
            var sqlExErrorCode = sqlEx.ErrorCode;
            var isDeadLock = sqlExNumber == 1205;
            // assert
            var messageParts = new[]
                                   {
                                       "was deadlocked on lock",
                                       "resources with another process and has been chosen as the deadlock victim. rerun the transaction"
                                   };
            var currentMessage = e.Message.ToLower();
            var isMessageDeadlock = !messageParts.Where(msgPart => !currentMessage.Contains(msgPart)).Any();

            if (sqlEx != null && isMessageDeadlock != isDeadLock)
                throw new Exception(String.Concat("Incorrect deadlock analysis",
                    ". Number: ", sqlExNumber,
                    ". ErrorCode: ", sqlExErrorCode,
                    ". Errors.Count: ", sqlEx.Errors.Count,
                    ". Original message: ", e.Message), e);
            return isDeadLock;
        }
        private static Exception SavingExceptionHelper(NodeData data, Exception catchedEx)
        {
            var message = "The content cannot be saved.";
            if (catchedEx.Message.StartsWith("Cannot insert duplicate key"))
            {
                message += " A content with the name you specified already exists.";

                var appExc = new NodeAlreadyExistsException(message, catchedEx); // new ApplicationException(message, catchedEx);
                appExc.Data.Add("NodeId", data.Id);
                appExc.Data.Add("Path", data.Path);
                appExc.Data.Add("OriginalPath", data.OriginalPath);

                appExc.Data.Add("ErrorCode", "ExistingNode");
                return appExc;
            }
            return catchedEx;
        }

        // ====================================================================== Index document save / load operations

        public static IndexDocumentData SaveIndexDocument(Node node, bool skipBinaries, bool isNew, out bool hasBinary)
        {
            if (node.Id == 0)
                throw new NotSupportedException("Cannot save the indexing information before node is not saved.");

            node.MakePrivateData(); // this is important because version timestamp will be changed.

            var doc = IndexDocumentProvider.GetIndexDocument(node, skipBinaries, isNew, out hasBinary);
            var serializedIndexDocument = doc.Serialize();

            DataStore.SaveIndexDocumentAsync(node.Data, doc).Wait();

            return CreateIndexDocumentData(node, doc, serializedIndexDocument);
        }
        public static IndexDocumentData SaveIndexDocument(Node node, IndexDocumentData indexDocumentData)
        {
            if (node.Id == 0)
                throw new NotSupportedException("Cannot save the indexing information before node is not saved.");

            node.MakePrivateData(); // this is important because version timestamp will be changed.

            var completedDocument = IndexDocumentProvider.CompleteIndexDocument(node, indexDocumentData.IndexDocument);
            var serializedIndexDocument = completedDocument.Serialize();

            DataStore.SaveIndexDocumentAsync(node.Data, completedDocument).Wait();

            return CreateIndexDocumentData(node, completedDocument, serializedIndexDocument);
        }

        internal static IndexDocumentData CreateIndexDocumentData(Node node, IndexDocument indexDocument, string serializedIndexDocument)
        {
            return new IndexDocumentData(indexDocument, serializedIndexDocument)
            {
                NodeTypeId = node.NodeTypeId,
                VersionId = node.VersionId,
                NodeId = node.Id,
                ParentId = node.ParentId,
                Path = node.Path,
                IsSystem = node.IsSystem,
                IsLastDraft = node.IsLatestVersion,
                IsLastPublic = node.IsLastPublicVersion,
                NodeTimestamp = node.NodeTimestamp,
                VersionTimestamp = node.VersionTimestamp
            };
        }
    }
}
