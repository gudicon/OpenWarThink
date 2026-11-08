using UnityEngine;

namespace MW2
{
    /// <summary>World_LoadObj10070785 copies OBJ body+44 to model+2, used by Material_Shade1001afb0.</summary>
    public sealed class Mw2ImageNode : MonoBehaviour
    {
        public int Flags;
        void OnEnable() => Apply();
        public void Apply()
        {
            if (!TryGetComponent<Renderer>(out var r)) return;
            var block = new MaterialPropertyBlock(); r.GetPropertyBlock(block);
            block.SetFloat("_Mw2EnhancedFlags", Flags & 0xffff); block.SetFloat("_Mw2EnhancedOverride", 1);
            r.SetPropertyBlock(block);
        }
    }
}
