using System.Collections.Generic;
using System.IO;
using UnityEditor.Experimental.SceneManagement;
using UnityEditor.SceneManagement;
using UnityEditor.VersionControl;
using UnityEngine.AI;
using UnityEngine;
using NavMeshPlus.Extensions;
using UnityEditor;
using NavMeshPlus.Components;

namespace NavMeshPlus.Editors.Components
{
    public class NavMeshAssetManager : ScriptableSingleton<NavMeshAssetManager>
    {
        internal struct AsyncBakeOperation
        {
            public NavMeshSurface surface;
            public NavMeshData bakeData;
            public AsyncOperation bakeOperation;
        }

        List<AsyncBakeOperation> m_BakeOperations = new List<AsyncBakeOperation>();
        internal List<AsyncBakeOperation> GetBakeOperations() { return m_BakeOperations; }

        struct SavedPrefabNavMeshData
        {
            public NavMeshSurface surface;
            public NavMeshData navMeshData;
        }

        List<SavedPrefabNavMeshData> m_PrefabNavMeshDataAssets = new List<SavedPrefabNavMeshData>();

        static string GetAndEnsureTargetPath(NavMeshSurface surface)
        {
            // Create directory for the asset if it does not exist yet.
            var activeScenePath = surface.gameObject.scene.path;

            var targetPath = "Assets";
            if (!string.IsNullOrEmpty(activeScenePath))
            {
                targetPath = Path.Combine(Path.GetDirectoryName(activeScenePath), Path.GetFileNameWithoutExtension(activeScenePath));
            }
            else
            {
                var prefabStage = PrefabStageUtility.GetPrefabStage(surface.gameObject);
                var isPartOfPrefab = prefabStage != null && prefabStage.IsPartOfPrefabContents(surface.gameObject);

                if (isPartOfPrefab)
                {
#if UNITY_2020_1_OR_NEWER
                    var assetPath = prefabStage.assetPath;
#else
                    var assetPath = prefabStage.prefabAssetPath;
#endif
                    if (!string.IsNullOrEmpty(assetPath))
                    {
                        var prefabDirectoryName = Path.GetDirectoryName(assetPath);
                        if (!string.IsNullOrEmpty(prefabDirectoryName))
                            targetPath = prefabDirectoryName;
                    }
                }
            }
            if (!Directory.Exists(targetPath))
                Directory.CreateDirectory(targetPath);
            return targetPath.Replace('\\', '/');
        }

        static string GetCanonicalNavMeshAssetPath(NavMeshSurface surface)
        {
            var targetPath = GetAndEnsureTargetPath(surface);
            return Path.Combine(targetPath, "NavMesh-" + surface.name + ".asset").Replace('\\', '/');
        }

        static void CheckoutAssetPath(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath) || !assetPath.StartsWith("Assets"))
                return;

            if (Provider.enabled && Provider.isActive)
            {
                Provider.Checkout(assetPath, CheckoutMode.Both).Wait();
                return;
            }

            // Fallback when VCS is unavailable: clear read-only so write/delete can succeed.
            var fileSystemPath = Path.GetFullPath(assetPath);
            if (File.Exists(fileSystemPath))
            {
                var attributes = File.GetAttributes(fileSystemPath);
                if ((attributes & FileAttributes.ReadOnly) != 0)
                    File.SetAttributes(fileSystemPath, attributes & ~FileAttributes.ReadOnly);
            }
        }

        /// <summary>
        /// Only talks to VCS when the file is still read-only (not already checked out / writable).
        /// </summary>
        static void EnsureAssetWritable(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath) || !assetPath.StartsWith("Assets"))
                return;

            var fileSystemPath = Path.GetFullPath(assetPath);
            if (File.Exists(fileSystemPath) && (File.GetAttributes(fileSystemPath) & FileAttributes.ReadOnly) == 0)
                return;

            CheckoutAssetPath(assetPath);
        }

        static void DeleteAssetWithCheckout(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath))
                return;

            if (string.IsNullOrEmpty(AssetDatabase.AssetPathToGUID(assetPath)))
                return;

            EnsureAssetWritable(assetPath);
            AssetDatabase.DeleteAsset(assetPath);
        }

        static bool IsPersistentAsset(Object assetObject)
        {
            return assetObject && !string.IsNullOrEmpty(AssetDatabase.GetAssetPath(assetObject));
        }

        /// <summary>
        /// Prefer the surface's owned persistent asset, else an existing canonical asset, else a new in-memory NavMeshData.
        /// </summary>
        NavMeshData ResolveBakeTarget(NavMeshSurface surface)
        {
            var owned = GetNavMeshAssetToDelete(surface);
            if (owned != null && IsPersistentAsset(owned))
                return owned;

            var canonicalPath = GetCanonicalNavMeshAssetPath(surface);
            var canonicalAsset = AssetDatabase.LoadAssetAtPath<NavMeshData>(canonicalPath);
            if (canonicalAsset != null)
                return canonicalAsset;

            return InitializeBakeData(surface);
        }

        /// <summary>
        /// One-time rename to the canonical filename when free. Keeps the same GUID (P4 edit/move, not delete+add).
        /// </summary>
        static void EnsureCanonicalAssetPath(NavMeshData navMeshData, NavMeshSurface surface)
        {
            var currentPath = AssetDatabase.GetAssetPath(navMeshData);
            if (string.IsNullOrEmpty(currentPath))
                return;

            var canonicalPath = GetCanonicalNavMeshAssetPath(surface);
            if (currentPath.Replace('\\', '/') == canonicalPath)
                return;

            if (!string.IsNullOrEmpty(AssetDatabase.AssetPathToGUID(canonicalPath)))
                return;

            EnsureAssetWritable(currentPath);
            AssetDatabase.MoveAsset(currentPath, canonicalPath);
        }

        static void CreateNavMeshAsset(NavMeshSurface surface)
        {
            var combinedAssetPath = GetCanonicalNavMeshAssetPath(surface);
            if (!string.IsNullOrEmpty(AssetDatabase.AssetPathToGUID(combinedAssetPath)))
            {
                // Canonical path already occupied — should have been resolved as bake target.
                return;
            }

            AssetDatabase.CreateAsset(surface.navMeshData, combinedAssetPath);
        }

        NavMeshData GetNavMeshAssetToDelete(NavMeshSurface navSurface)
        {
            if (PrefabUtility.IsPartOfPrefabInstance(navSurface) && !PrefabUtility.IsPartOfModelPrefab(navSurface))
            {
                // Don't allow deleting/mutating the asset belonging to the prefab parent
                var parentSurface = PrefabUtility.GetCorrespondingObjectFromSource(navSurface) as NavMeshSurface;
                if (parentSurface && navSurface.navMeshData == parentSurface.navMeshData)
                    return null;
            }

            // Do not delete the NavMeshData asset referenced from a prefab until the prefab is saved
            var prefabStage = PrefabStageUtility.GetPrefabStage(navSurface.gameObject);
            var isPartOfPrefab = prefabStage != null && prefabStage.IsPartOfPrefabContents(navSurface.gameObject);
            if (isPartOfPrefab && IsCurrentPrefabNavMeshDataStored(navSurface))
                return null;

            return navSurface.navMeshData;
        }

        void ClearSurface(NavMeshSurface navSurface)
        {
            var hasNavMeshData = navSurface.navMeshData != null;
            StoreNavMeshDataIfInPrefab(navSurface);

            var assetToDelete = GetNavMeshAssetToDelete(navSurface);
            navSurface.RemoveData();

            if (hasNavMeshData)
            {
                SetNavMeshData(navSurface, null);
                EditorSceneManager.MarkSceneDirty(navSurface.gameObject.scene);
            }

            if (assetToDelete && IsPersistentAsset(assetToDelete))
                DeleteAssetWithCheckout(AssetDatabase.GetAssetPath(assetToDelete));
        }

        public void StartBakingSurfaces(UnityEngine.Object[] surfaces)
        {
            // Remove first to avoid double registration of the callback
            EditorApplication.update -= UpdateAsyncBuildOperations;
            EditorApplication.update += UpdateAsyncBuildOperations;

            foreach (NavMeshSurface surf in surfaces)
            {
                StoreNavMeshDataIfInPrefab(surf);

                var oper = new AsyncBakeOperation();
                oper.bakeData = ResolveBakeTarget(surf);
                var assetPath = AssetDatabase.GetAssetPath(oper.bakeData);
                if (!string.IsNullOrEmpty(assetPath))
                    EnsureAssetWritable(assetPath);

                oper.bakeOperation = surf.UpdateNavMesh(oper.bakeData);
                oper.surface = surf;

                m_BakeOperations.Add(oper);
            }
        }

        public void BakeSurfacesBlocking(IEnumerable<NavMeshSurface> surfaces)
        {
            foreach (var surface in surfaces)
            {
                StoreNavMeshDataIfInPrefab(surface);

                var bakeData = ResolveBakeTarget(surface);
                var assetPath = AssetDatabase.GetAssetPath(bakeData);
                if (!string.IsNullOrEmpty(assetPath))
                    EnsureAssetWritable(assetPath);

                surface.UpdateNavMeshBlocking(bakeData);
                PersistBakedNavMesh(surface, bakeData);
            }

            AssetDatabase.SaveAssets();
        }

        static NavMeshData InitializeBakeData(NavMeshSurface surface)
        {
            var emptySources = new List<NavMeshBuildSource>();
            var emptyBounds = new Bounds();
            return UnityEngine.AI.NavMeshBuilder.BuildNavMeshData(surface.GetBuildSettings(), emptySources, emptyBounds
                , surface.transform.position, surface.transform.rotation);
        }

        void UpdateAsyncBuildOperations()
        {
            foreach (var oper in m_BakeOperations)
            {
                if (oper.surface == null || oper.bakeOperation == null)
                    continue;

                if (oper.bakeOperation.isDone)
                {
                    PersistBakedNavMesh(oper.surface, oper.bakeData);
                }
            }
            m_BakeOperations.RemoveAll(o => o.bakeOperation == null || o.bakeOperation.isDone);
            if (m_BakeOperations.Count == 0)
            {
                EditorApplication.update -= UpdateAsyncBuildOperations;
                AssetDatabase.SaveAssets();
            }
        }

        private void PersistBakedNavMesh(NavMeshSurface surface, NavMeshData bakeData)
        {
            var wasPersistent = IsPersistentAsset(bakeData);

            surface.RemoveData();
            SetNavMeshData(surface, bakeData);

            if (surface.isActiveAndEnabled)
                surface.AddData();

            if (!wasPersistent)
                CreateNavMeshAsset(surface);
            else
                EnsureCanonicalAssetPath(bakeData, surface);

            EditorUtility.SetDirty(bakeData);
            EditorSceneManager.MarkSceneDirty(surface.gameObject.scene);
        }

        public bool IsSurfaceBaking(NavMeshSurface surface)
        {
            if (surface == null)
                return false;

            foreach (var oper in m_BakeOperations)
            {
                if (oper.surface == null || oper.bakeOperation == null)
                    continue;

                if (oper.surface == surface)
                    return !oper.bakeOperation.isDone;
            }

            return false;
        }

        public void ClearSurfaces(UnityEngine.Object[] surfaces)
        {
            foreach (NavMeshSurface s in surfaces)
                ClearSurface(s);
        }

        static void SetNavMeshData(NavMeshSurface navSurface, NavMeshData navMeshData)
        {
            var so = new SerializedObject(navSurface);
            var navMeshDataProperty = so.FindProperty("m_NavMeshData");
            navMeshDataProperty.objectReferenceValue = navMeshData;
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        void StoreNavMeshDataIfInPrefab(NavMeshSurface surfaceToStore)
        {
            var prefabStage = PrefabStageUtility.GetPrefabStage(surfaceToStore.gameObject);
            var isPartOfPrefab = prefabStage != null && prefabStage.IsPartOfPrefabContents(surfaceToStore.gameObject);
            if (!isPartOfPrefab)
                return;

            // check if data has already been stored for this surface
            foreach (var storedAssetInfo in m_PrefabNavMeshDataAssets)
                if (storedAssetInfo.surface == surfaceToStore)
                    return;

            if (m_PrefabNavMeshDataAssets.Count == 0)
            {
                PrefabStage.prefabSaving -= DeleteStoredNavMeshDataAssetsForOwnedSurfaces;
                PrefabStage.prefabSaving += DeleteStoredNavMeshDataAssetsForOwnedSurfaces;

                PrefabStage.prefabStageClosing -= ForgetUnsavedNavMeshDataChanges;
                PrefabStage.prefabStageClosing += ForgetUnsavedNavMeshDataChanges;
            }

            var isDataOwner = true;
            if (PrefabUtility.IsPartOfPrefabInstance(surfaceToStore) && !PrefabUtility.IsPartOfModelPrefab(surfaceToStore))
            {
                var basePrefabSurface = PrefabUtility.GetCorrespondingObjectFromSource(surfaceToStore) as NavMeshSurface;
                isDataOwner = basePrefabSurface == null || surfaceToStore.navMeshData != basePrefabSurface.navMeshData;
            }
            m_PrefabNavMeshDataAssets.Add(new SavedPrefabNavMeshData { surface = surfaceToStore, navMeshData = isDataOwner ? surfaceToStore.navMeshData : null });
        }

        bool IsCurrentPrefabNavMeshDataStored(NavMeshSurface surface)
        {
            if (surface == null)
                return false;

            foreach (var storedAssetInfo in m_PrefabNavMeshDataAssets)
            {
                if (storedAssetInfo.surface == surface)
                    return storedAssetInfo.navMeshData == surface.navMeshData;
            }

            return false;
        }

        void DeleteStoredNavMeshDataAssetsForOwnedSurfaces(GameObject gameObjectInPrefab)
        {
            // Debug.LogFormat("DeleteStoredNavMeshDataAsset() when saving prefab {0}", gameObjectInPrefab.name);

            var surfaces = gameObjectInPrefab.GetComponentsInChildren<NavMeshSurface>(true);
            foreach (var surface in surfaces)
                DeleteStoredPrefabNavMeshDataAsset(surface);
        }

        void DeleteStoredPrefabNavMeshDataAsset(NavMeshSurface surface)
        {
            for (var i = m_PrefabNavMeshDataAssets.Count - 1; i >= 0; i--)
            {
                var storedAssetInfo = m_PrefabNavMeshDataAssets[i];
                if (storedAssetInfo.surface == surface)
                {
                    var storedNavMeshData = storedAssetInfo.navMeshData;
                    if (storedNavMeshData != null && storedNavMeshData != surface.navMeshData)
                    {
                        var assetPath = AssetDatabase.GetAssetPath(storedNavMeshData);
                        DeleteAssetWithCheckout(assetPath);
                    }

                    m_PrefabNavMeshDataAssets.RemoveAt(i);
                    break;
                }
            }

            if (m_PrefabNavMeshDataAssets.Count == 0)
            {
                PrefabStage.prefabSaving -= DeleteStoredNavMeshDataAssetsForOwnedSurfaces;
                PrefabStage.prefabStageClosing -= ForgetUnsavedNavMeshDataChanges;
            }
        }

        void ForgetUnsavedNavMeshDataChanges(PrefabStage prefabStage)
        {
            // Debug.Log("On prefab closing - forget about this object's surfaces and stop caring about prefab saving");

            if (prefabStage == null)
                return;

            var allSurfacesInPrefab = prefabStage.prefabContentsRoot.GetComponentsInChildren<NavMeshSurface>(true);
            NavMeshSurface surfaceInPrefab = null;
            var index = 0;
            do
            {
                if (allSurfacesInPrefab.Length > 0)
                    surfaceInPrefab = allSurfacesInPrefab[index];

                for (var i = m_PrefabNavMeshDataAssets.Count - 1; i >= 0; i--)
                {
                    var storedPrefabInfo = m_PrefabNavMeshDataAssets[i];
                    if (storedPrefabInfo.surface == null)
                    {
                        // Debug.LogFormat("A surface from the prefab got deleted after it has baked a new NavMesh but it hasn't saved it. Now the unsaved asset gets deleted. ({0})", storedPrefabInfo.navMeshData);

                        // surface got deleted, thus delete its initial NavMeshData asset
                        if (storedPrefabInfo.navMeshData != null)
                        {
                            var assetPath = AssetDatabase.GetAssetPath(storedPrefabInfo.navMeshData);
                            DeleteAssetWithCheckout(assetPath);
                        }

                        m_PrefabNavMeshDataAssets.RemoveAt(i);
                    }
                    else if (surfaceInPrefab != null && storedPrefabInfo.surface == surfaceInPrefab)
                    {
                        //Debug.LogFormat("The surface {0} from the prefab was storing the original navmesh data and now will be forgotten", surfaceInPrefab);

                        var baseSurface = PrefabUtility.GetCorrespondingObjectFromSource(surfaceInPrefab) as NavMeshSurface;
                        if (baseSurface == null || surfaceInPrefab.navMeshData != baseSurface.navMeshData)
                        {
                            var assetPath = AssetDatabase.GetAssetPath(surfaceInPrefab.navMeshData);
                            DeleteAssetWithCheckout(assetPath);

                            //Debug.LogFormat("The surface {0} from the prefab has baked new NavMeshData but did not save this change so the asset has been now deleted. ({1})",
                            //    surfaceInPrefab, assetPath);
                        }

                        m_PrefabNavMeshDataAssets.RemoveAt(i);
                    }
                }
            } while (++index < allSurfacesInPrefab.Length);

            if (m_PrefabNavMeshDataAssets.Count == 0)
            {
                PrefabStage.prefabSaving -= DeleteStoredNavMeshDataAssetsForOwnedSurfaces;
                PrefabStage.prefabStageClosing -= ForgetUnsavedNavMeshDataChanges;
            }
        }
    }
}
