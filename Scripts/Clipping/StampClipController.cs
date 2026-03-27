using System;
using System.Collections.Generic;
using EasyButtons;
using UnityEngine;

namespace VIRTOSHA.ZAnatomy.Clipping
{
    /// <summary>
    /// Activates configured stamp GameObjects and publishes active clipping sphere matrices
    /// to <see cref="StampClipCoordinator"/> as a named source.
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("VIRTOSHA/Z-Anatomy/Stamp Clip Controller")]
    public class StampClipController : MonoBehaviour
    {
        [Header("Integration")]
        [SerializeField, Tooltip("Global stamp clip coordinator that receives active stamp controller stamps.")]
        private StampClipCoordinator stampClipCoordinator;

        [Header("Stamp Sources")]
        [SerializeField]
        private bool forceStampsInactiveOnStart = true;

        [SerializeField]
        private List<GameObject> stamps = new List<GameObject>();

        [Header("Stamp Groups")]
        [SerializeField]
        private List<StampGroup> stampGroups = new List<StampGroup>();

        public IReadOnlyList<GameObject> Stamps => stamps;
        public IReadOnlyList<StampGroup> StampGroups => stampGroups;

        private void Awake()
        {
            EnsureCoordinatorReference();

            if (forceStampsInactiveOnStart)
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
                Debug.LogWarning($"[{nameof(StampClipController)}:{name}] Stamp index {index} is out of range.", this);
                return;
            }

            ActivateObject(stamps[index], $"stamp index {index}");
        }

        public void ActivateGroup(int groupIndex)
        {
            if (groupIndex < 0 || groupIndex >= stampGroups.Count)
            {
                Debug.LogWarning($"[{nameof(StampClipController)}:{name}] Group index {groupIndex} is out of range.", this);
                return;
            }

            StampGroup group = stampGroups[groupIndex];
            if (group == null)
            {
                Debug.LogWarning($"[{nameof(StampClipController)}:{name}] Group index {groupIndex} is null.", this);
                return;
            }

            IReadOnlyList<GameObject> groupStamps = group.StampObjects;
            if (groupStamps == null)
            {
                Debug.LogWarning($"[{nameof(StampClipController)}:{name}] Group '{group.GroupName}' has no stamp list.", this);
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
                Debug.LogWarning($"[{nameof(StampClipController)}:{name}] Group name is empty.", this);
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

            Debug.LogWarning($"[{nameof(StampClipController)}:{name}] No group named '{groupName}' found.", this);
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
                Debug.LogWarning($"[{nameof(StampClipController)}:{name}] Missing stamp object for {source}.", this);
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
