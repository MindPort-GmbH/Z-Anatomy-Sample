using System;
using System.Collections.Generic;
using UnityEngine;

namespace VIRTOSHA.ZAnatomy.Clipping
{
    /// <summary>
    /// Shared configuration and coordinator helpers for stamp clip sources.
    /// </summary>
    public abstract class StampClipSourceBase : MonoBehaviour
    {
        [Header("Coordination")]
        [SerializeField, Tooltip("Global stamp clip coordinator that receives this source and target routing.")]
        private StampClipCoordinator stampClipCoordinator;

        [SerializeField, Tooltip("If enabled, this source checks for transform/target changes every frame in Update.")]
        protected bool continuousSync = true;

        [Header("Renders to Clip")]
        [SerializeField, Tooltip("Toggles whether clipping will apply to shared materials or material instances (default) on renderers within the renderers list. This cannot be altered when renderers are already specified.")]
        protected bool applyToSharedMaterial;

        /// <summary>
        /// Toggles whether clipping will apply to shared materials or material instances (default) on renderers within the renderers list.
        /// This cannot be altered when renderers are already specified.
        /// </summary>
        /// <remarks>
        /// Applying to shared materials will allow for GPU instancing to batch calls between Renderers.
        /// </remarks>
        public bool ApplyToSharedMaterial
        {
            get => applyToSharedMaterial;
            set
            {
                if (value != applyToSharedMaterial)
                {
                    if (renderers.Count > 0)
                    {
                        throw new InvalidOperationException("Cannot change material applied to after renderers have been added.");
                    }

                    applyToSharedMaterial = value;
                }
            }
        }

        [SerializeField, Tooltip("The renderer(s) that should be affected by the cutter.")]
        protected List<Renderer> renderers = new List<Renderer>();

        [Header("Materials to Clip")]
        [SerializeField, Tooltip("The material(s) that should be affected by the cutter. Materials on renderers within the renderers list do not need to be added to this list.")]
        protected List<Material> materials = new List<Material>();

        private readonly HashSet<int> dedupeIds = new HashSet<int>();
        private bool missingCoordinatorWarningLogged;

        protected virtual void Awake()
        {
            EnsureCoordinatorReference();
        }

        protected virtual void Update()
        {
            if (!ShouldSync())
            {
                return;
            }

            PushUpdateToCoordinator();
        }

        protected virtual bool ShouldSync()
        {
            return continuousSync;
        }

        protected abstract void PushUpdateToCoordinator();

        protected void EnsureCoordinatorReference()
        {
            if (stampClipCoordinator == null)
            {
                stampClipCoordinator = FindAnyObjectByType<StampClipCoordinator>();
            }
        }

        protected bool TryGetCoordinator(out StampClipCoordinator coordinator)
        {
            EnsureCoordinatorReference();
            coordinator = stampClipCoordinator;
            if (coordinator != null)
            {
                missingCoordinatorWarningLogged = false;
                return true;
            }

            if (!missingCoordinatorWarningLogged)
            {
                Debug.LogWarning($"[{GetType().Name}:{name}] Missing {nameof(StampClipCoordinator)} reference.", this);
                missingCoordinatorWarningLogged = true;
            }

            return false;
        }

        protected void CollectConfiguredTargets(List<Renderer> rendererBuffer, List<Material> materialBuffer)
        {
            rendererBuffer.Clear();
            materialBuffer.Clear();

            if (renderers != null)
            {
                dedupeIds.Clear();
                for (int i = 0; i < renderers.Count; i++)
                {
                    Renderer renderer = renderers[i];
                    if (renderer == null)
                    {
                        continue;
                    }

                    int id = renderer.GetInstanceID();
                    if (dedupeIds.Add(id))
                    {
                        rendererBuffer.Add(renderer);
                    }
                }
            }

            if (materials != null)
            {
                dedupeIds.Clear();
                for (int i = 0; i < materials.Count; i++)
                {
                    Material material = materials[i];
                    if (material == null)
                    {
                        continue;
                    }

                    int id = material.GetInstanceID();
                    if (dedupeIds.Add(id))
                    {
                        materialBuffer.Add(material);
                    }
                }
            }
        }

        public void AppendConfiguredTargets(HashSet<Renderer> rendererTargets, HashSet<Material> materialTargets)
        {
            if (rendererTargets != null && renderers != null)
            {
                for (int i = 0; i < renderers.Count; i++)
                {
                    Renderer renderer = renderers[i];
                    if (renderer != null)
                    {
                        rendererTargets.Add(renderer);
                    }
                }
            }

            if (materialTargets != null && materials != null)
            {
                for (int i = 0; i < materials.Count; i++)
                {
                    Material material = materials[i];
                    if (material != null)
                    {
                        materialTargets.Add(material);
                    }
                }
            }
        }
    }
}
