using HarmonyLib;
using UnityEngine;

namespace Companions
{
    /// <summary>
    /// Fixes "mesh data size and vertex stride" error when VisEquipment swaps
    /// between male/female body meshes on companion SkinnedMeshRenderers.
    /// Clearing sharedMesh before the swap forces Unity to reallocate the GPU
    /// vertex buffer with the correct format for the incoming mesh.
    /// </summary>
    [HarmonyPatch(typeof(VisEquipment), "UpdateBaseModel")]
    static class VisEquipment_UpdateBaseModel_Patch
    {
        private static readonly int ModelIndexHash =
            StringExtensionMethods.GetStableHashCode("ModelIndex");

        static void Prefix(VisEquipment __instance)
        {
            if (!CompanionSetup.IsCompanionVisEquip(__instance))
                return;

            if (__instance.m_models == null || __instance.m_models.Length == 0 ||
                __instance.m_bodyModel == null)
                return;

            // Determine target model index (mirrors vanilla UpdateBaseModel logic)
            var nview = __instance.GetComponent<ZNetView>();
            int targetIndex = 0;
            if (nview?.GetZDO() != null)
                targetIndex = nview.GetZDO().GetInt(ModelIndexHash);

            if (targetIndex < 0 || targetIndex >= __instance.m_models.Length)
                return;

            var model = __instance.m_models[targetIndex];
            if (model == null) return;
            var targetMesh = model.m_mesh;
            if (targetMesh == null) return;

            // If the mesh needs to change, clear it first to force GPU vertex
            // buffer reallocation — prevents the stride mismatch error.
            if (__instance.m_bodyModel.sharedMesh != null &&
                __instance.m_bodyModel.sharedMesh != targetMesh)
            {
                __instance.m_bodyModel.sharedMesh = null;
            }
        }
    }
}
