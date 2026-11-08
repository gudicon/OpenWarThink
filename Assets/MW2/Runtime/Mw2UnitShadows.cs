using System.Collections.Generic;
using UnityEngine;

namespace MW2
{
    /// <summary>
    /// Unit shadows (Glow type 1). Mech_InitMovement 0x100380d0 takes a shadow record for every unit it starts
    /// (Glow_Take 0x1005c2eb, gamepiece +0x140); Mech_Destroy 0x10035ef6 and FUN_1003bb8a release it. Glow_Update
    /// 0x1005c0e6 keeps the record at the unit's x, z and ground height (gamepiece +0x50, +0x74, +0x58). With Shadows on
    /// (DAT_1009f71c, main menu) FUN_1005d52c draws it by FUN_1005d44e: every polygon of every object of the unit's tree
    /// flattened to that height, plus 5 units unless it is 0 (FUN_1005d2ec), black with alpha 0x80 (MW2/Shadow).
    /// Here the tree is the unit's enabled mesh renderers (the current level of detail).
    /// </summary>
    public class Mw2UnitShadows : MonoBehaviour
    {
        public Material Material;
        const float Units = Formats.PolyModel.UnitsToMeters;
        static readonly int ShadowYId = Shader.PropertyToID("_ShadowY");
        readonly List<MeshRenderer> _renderers = new List<MeshRenderer>();
        MaterialPropertyBlock _block;

        void LateUpdate()
        {
            if (Material == null || !Mw2Options.Shadows) return;
            _block ??= new MaterialPropertyBlock();
            foreach (var unit in Mw2MechCombat.All)
            {
                if (unit == null || unit.Destroyed) continue;
                float ground = unit.TryGetComponent<Mw2MechController>(out var controller)
                    ? controller.GroundUnits * Units : Mw2Ground.Height(unit.transform.position);
                // FUN_1005d2ec: the record's height + 5 units, or 0 when the height is 0.
                _block.SetFloat(ShadowYId, ground == 0f ? 0f : ground + 5f * Units);
                unit.GetComponentsInChildren(false, _renderers);
                foreach (var r in _renderers)
                {
                    if (!r.enabled || !r.TryGetComponent<MeshFilter>(out var filter) || filter.sharedMesh == null) continue;
                    var mesh = filter.sharedMesh;
                    var matrix = r.localToWorldMatrix;
                    for (int s = 0; s < mesh.subMeshCount; s++)
                        Graphics.DrawMesh(mesh, matrix, Material, r.gameObject.layer, null, s, _block, false, false);
                }
            }
        }
    }
}
