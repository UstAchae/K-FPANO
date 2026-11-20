using UnityEngine;
// Serialization stub: same class name, namespace and fields as the game's, so the bundle binds to the game class.
namespace EFT.Visual
{
    public class NightVisionDevice : MonoBehaviour
    {
        [SerializeField] private Light _light;
        public void Set(Light light) { _light = light; }
    }
}
