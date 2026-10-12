using UnityEngine;
using GungeonTogether.Networking;
using GungeonTogether.Networking.Packets;
using System.Reflection;

namespace GungeonTogether.Networking.Sync
{
    public class LoadingSyncManager : MonoBehaviour
    {
        private static LoadingSyncManager _instance;
        public static LoadingSyncManager Instance
        {
            get
            {
                if (_instance == null)
                {
                    GameObject go = new GameObject("LoadingSyncManager");
                    _instance = go.AddComponent<LoadingSyncManager>();
                    DontDestroyOnLoad(go);
                }
                return _instance;
            }
        }

        private bool _wasLoading = false;
        private bool _isLoading = false;

        private void Update()
        {
            if (!NetworkManager.Instance.IsHost) return;

            // Detect loading state via GameManager or SceneManager
            var gm = ETGReflectionHelper.GetGameManager();
            if (gm == null) return;

            _isLoading = ETGReflectionHelper.IsLevelLoading();

            if (_isLoading != _wasLoading)
            {
                _wasLoading = _isLoading;
                var packet = new LoadingStatePacket { IsLoading = _isLoading };
                NetworkManager.Instance.Host.Broadcast(packet, reliable: true);
                Debug.Log($"[LoadingSync] Host loading state changed: {_isLoading}");
            }
        }

        // Client apply (handled in NetworkManager)
        public void ApplyLoadingState(bool isLoading)
        {
            // If client is loading, we could show a message; but for now, we just wait.
            // We'll also delay position sync until loading finishes.
            _isClientLoading = isLoading;
            WorldSyncManager.Instance.SetClientLoading(isLoading);
        }

        private bool _isClientLoading = false;
        public bool IsClientLoading => _isClientLoading;
    }
}
