using System;
using System.Collections.Generic;
using EasyButtons;
using UnityEngine;

namespace VIRTOSHA.ZAnatomy
{
    [DisallowMultipleComponent]
    [AddComponentMenu("VIRTOSHA/Z-Anatomy/Cut Visualisation")]
    public class CutVisualisation : MonoBehaviour
    {
        [Serializable]
        public class StampGroup
        {
            [SerializeField]
            private string groupName = "Default";

            [SerializeField]
            private List<GameObject> stampObjects = new List<GameObject>();

            public string GroupName => groupName;
            public IReadOnlyList<GameObject> StampObjects => stampObjects;

            public void SetGroupName(string value)
            {
                groupName = string.IsNullOrWhiteSpace(value) ? "Default" : value;
            }

            public void SetStampObjects(IEnumerable<GameObject> objects)
            {
                stampObjects = new List<GameObject>();
                if (objects == null)
                {
                    return;
                }

                HashSet<GameObject> unique = new HashSet<GameObject>();
                foreach (GameObject stamp in objects)
                {
                    if (stamp != null && unique.Add(stamp))
                    {
                        stampObjects.Add(stamp);
                    }
                }
            }
        }

        [Header("Stamp Sources")]
        [SerializeField]
        private List<GameObject> stamps = new List<GameObject>();

        [Header("Stamp Groups")]
        [SerializeField]
        private List<StampGroup> stampGroups = new List<StampGroup>();

        [Header("Integration")]
        [SerializeField, Tooltip("Global stamp clip coordinator that receives active cut visualisation stamps.")]
        private StampClipCoordinator stampClipCoordinator;

        [Header("Startup")]
        [SerializeField]
        private bool forceConfiguredStampsInactiveOnStartup = true;

        public IReadOnlyList<GameObject> Stamps => stamps;
        public IReadOnlyList<StampGroup> StampGroups => stampGroups;

        private void Awake()
        {
            EnsureCoordinatorReference();

            if (forceConfiguredStampsInactiveOnStartup)
            {
                DeactivateAllConfiguredStamps();
            }

            NotifyCoordinator();
        }

        private void OnDisable()
        {
            ClearCoordinatorSource();
        }

        private void OnDestroy()
        {
            ClearCoordinatorSource();
        }

        private void OnValidate()
        {
            stamps ??= new List<GameObject>();
            stampGroups ??= new List<StampGroup>();
            EnsureCoordinatorReference();

            for (int i = stampGroups.Count - 1; i >= 0; i--)
            {
                if (stampGroups[i] == null)
                {
                    stampGroups.RemoveAt(i);
                }
            }
        }

        public void ActivateStamp(int index)
        {
            if (index < 0 || index >= stamps.Count)
            {
                Debug.LogWarning($"[{nameof(CutVisualisation)}:{name}] Stamp index {index} is out of range.", this);
                return;
            }

            ActivateObject(stamps[index], $"stamp index {index}");
        }

        public void ActivateGroup(int groupIndex)
        {
            if (groupIndex < 0 || groupIndex >= stampGroups.Count)
            {
                Debug.LogWarning($"[{nameof(CutVisualisation)}:{name}] Group index {groupIndex} is out of range.", this);
                return;
            }

            StampGroup group = stampGroups[groupIndex];
            if (group == null)
            {
                Debug.LogWarning($"[{nameof(CutVisualisation)}:{name}] Group index {groupIndex} is null.", this);
                return;
            }

            IReadOnlyList<GameObject> groupStamps = group.StampObjects;
            if (groupStamps == null)
            {
                Debug.LogWarning($"[{nameof(CutVisualisation)}:{name}] Group '{group.GroupName}' has no stamp list.", this);
                return;
            }

            for (int i = 0; i < groupStamps.Count; i++)
            {
                ActivateObject(groupStamps[i], $"group '{group.GroupName}'");
            }
        }

        public void ActivateGroupByName(string groupName)
        {
            if (string.IsNullOrWhiteSpace(groupName))
            {
                Debug.LogWarning($"[{nameof(CutVisualisation)}:{name}] Group name is empty.", this);
                return;
            }

            for (int i = 0; i < stampGroups.Count; i++)
            {
                StampGroup group = stampGroups[i];
                if (group != null && string.Equals(group.GroupName, groupName, StringComparison.OrdinalIgnoreCase))
                {
                    ActivateGroup(i);
                    return;
                }
            }

            Debug.LogWarning($"[{nameof(CutVisualisation)}:{name}] No group named '{groupName}' found.", this);
        }

        [Button]
        public void ActivateAllStamps()
        {
            HashSet<GameObject> uniqueStamps = CollectConfiguredStamps();
            foreach (GameObject stamp in uniqueStamps)
            {
                ActivateObject(stamp, "all stamps");
            }
        }

        [Button]
        public void DeactivateAllStamps()
        {
            DeactivateAllConfiguredStamps();
        }

        [Button]
        public void ConfigureDefaultGroupFromStamps()
        {
            stamps ??= new List<GameObject>();
            stampGroups ??= new List<StampGroup>();

            StampGroup defaultGroup = stampGroups.Count > 0 ? stampGroups[0] : null;
            if (defaultGroup == null)
            {
                defaultGroup = new StampGroup();
                if (stampGroups.Count == 0)
                {
                    stampGroups.Add(defaultGroup);
                }
                else
                {
                    stampGroups[0] = defaultGroup;
                }
            }

            defaultGroup.SetGroupName("Default");
            defaultGroup.SetStampObjects(stamps);
            NotifyCoordinator();
        }

        private void DeactivateAllConfiguredStamps()
        {
            HashSet<GameObject> uniqueStamps = CollectConfiguredStamps();
            foreach (GameObject stamp in uniqueStamps)
            {
                if (stamp != null)
                {
                    stamp.SetActive(false);
                }
            }

            NotifyCoordinator();
        }

        private HashSet<GameObject> CollectConfiguredStamps()
        {
            HashSet<GameObject> uniqueStamps = new HashSet<GameObject>();

            if (stamps != null)
            {
                for (int i = 0; i < stamps.Count; i++)
                {
                    if (stamps[i] != null)
                    {
                        uniqueStamps.Add(stamps[i]);
                    }
                }
            }

            if (stampGroups != null)
            {
                for (int groupIndex = 0; groupIndex < stampGroups.Count; groupIndex++)
                {
                    StampGroup group = stampGroups[groupIndex];
                    if (group == null)
                    {
                        continue;
                    }

                    IReadOnlyList<GameObject> groupStamps = group.StampObjects;
                    if (groupStamps == null)
                    {
                        continue;
                    }

                    for (int stampIndex = 0; stampIndex < groupStamps.Count; stampIndex++)
                    {
                        if (groupStamps[stampIndex] != null)
                        {
                            uniqueStamps.Add(groupStamps[stampIndex]);
                        }
                    }
                }
            }

            return uniqueStamps;
        }

        private List<Matrix4x4> CollectActiveStampMatrices()
        {
            List<Matrix4x4> matrices = new List<Matrix4x4>();
            HashSet<GameObject> configuredStamps = CollectConfiguredStamps();

            foreach (GameObject source in configuredStamps)
            {
                if (source == null || !source.activeInHierarchy)
                {
                    continue;
                }

                BaseShaderClippingSphere[] spheres = source.GetComponentsInChildren<BaseShaderClippingSphere>(false);
                for (int i = 0; i < spheres.Length; i++)
                {
                    BaseShaderClippingSphere sphere = spheres[i];
                    if (sphere == null || !sphere.isActiveAndEnabled)
                    {
                        continue;
                    }

                    matrices.Add(sphere.transform.worldToLocalMatrix);
                }
            }

            return matrices;
        }

        private void ActivateObject(GameObject stampObject, string source)
        {
            if (stampObject == null)
            {
                Debug.LogWarning($"[{nameof(CutVisualisation)}:{name}] Missing stamp object for {source}.", this);
                return;
            }

            stampObject.SetActive(true);
            NotifyCoordinator();
        }

        private void EnsureCoordinatorReference()
        {
            if (stampClipCoordinator == null)
            {
                stampClipCoordinator = FindAnyObjectByType<StampClipCoordinator>();
            }
        }

        private void NotifyCoordinator()
        {
            EnsureCoordinatorReference();
            if (stampClipCoordinator == null)
            {
                return;
            }

            List<Matrix4x4> matrices = CollectActiveStampMatrices();
            if (matrices.Count == 0)
            {
                stampClipCoordinator.ClearSource(this);
                return;
            }

            stampClipCoordinator.SetSourceMatrices(this, matrices);
        }

        private void ClearCoordinatorSource()
        {
            EnsureCoordinatorReference();
            if (stampClipCoordinator == null)
            {
                return;
            }

            stampClipCoordinator.ClearSource(this);
        }
    }
}
