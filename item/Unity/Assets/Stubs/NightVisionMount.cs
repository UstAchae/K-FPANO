using UnityEngine;
// Serialization stub of the game's EFT.Visual.NightVisionMount.
namespace EFT.Visual
{
    public class NightVisionMount : MonoBehaviour
    {
        [SerializeField] private CurveRotator _rotator;
        public void Set(CurveRotator rotator) { _rotator = rotator; }
    }
}
