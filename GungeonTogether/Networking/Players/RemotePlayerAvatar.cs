using UnityEngine;

namespace GungeonTogether.Networking
{
    public class RemotePlayerAvatar : MonoBehaviour
    {
        private tk2dSprite _sprite;
        private Transform _spriteTransform;
        private Vector3 _targetPosition;
        private Vector2 _networkVelocity;
        private float _lastSnapshotTime;
        private uint _lastSequence;
        private bool _hasTarget;
        private float _nextVisualRetry;
        private float _nextCollectionRetry;
        private string _activeCollectionName = "";
        private bool _isDowned;
        private bool _isDodgeRolling;
        private Color _baseColor = Color.white;

        public static RemotePlayerAvatar Create(ulong steamId, Vector2 position, float rotation)
        {
            GameObject go = new GameObject("RemotePlayer_" + steamId);
            Object.DontDestroyOnLoad(go);
            RemotePlayerAvatar avatar = go.AddComponent<RemotePlayerAvatar>();
            avatar.Initialise(position, rotation);
            return avatar;
        }

        private void Initialise(Vector2 position, float rotation)
        {
            transform.position = new Vector3(position.x, position.y, 0f);
            transform.rotation = Quaternion.Euler(0f, 0f, rotation);
            _targetPosition = transform.position;
            _hasTarget = true;

            TryCreateVisual();
        }

        private bool TryCreateVisual()
        {
            if (_sprite != null) return true;
            PlayerController localPlayer = GetLocalPlayer();
            tk2dBaseSprite sourceSprite = localPlayer != null ? localPlayer.sprite : null;
            if (sourceSprite == null)
            {
                return false;
            }

            GameObject spriteObject = new GameObject("Sprite");
            spriteObject.transform.parent = transform;
            spriteObject.transform.localPosition = sourceSprite.transform.localPosition;
            spriteObject.transform.localRotation = sourceSprite.transform.localRotation;
            spriteObject.transform.localScale = sourceSprite.transform.localScale;
            _spriteTransform = spriteObject.transform;

            _sprite = spriteObject.AddComponent<tk2dSprite>();
            _sprite.SetSprite(sourceSprite.Collection, sourceSprite.spriteId);
            _sprite.HeightOffGround = sourceSprite.HeightOffGround;
            _sprite.SortingOrder = sourceSprite.SortingOrder;
            _sprite.FlipX = sourceSprite.FlipX;
            _sprite.scale = sourceSprite.scale;
            _baseColor = sourceSprite.color;
            _sprite.color = _baseColor;
            _activeCollectionName = sourceSprite.Collection != null ? sourceSprite.Collection.name : "";
            RefreshColor();
            return true;
        }

        public void Apply(Vector2 position, Vector2 velocity, float rotation, bool flipX, uint sequence, int spriteId, string spriteCollectionName, bool isDodgeRolling)
        {
            if (_lastSequence != 0 && sequence != 0 && sequence <= _lastSequence) return;
            _lastSequence = sequence;
            _targetPosition = new Vector3(position.x, position.y, 0f);
            _networkVelocity = velocity;
            _lastSnapshotTime = Time.realtimeSinceStartup;
            _hasTarget = true;
            transform.rotation = Quaternion.Euler(0f, 0f, rotation);

            if (_sprite != null)
            {
                _sprite.FlipX = flipX;
                if (!string.IsNullOrEmpty(spriteCollectionName) && spriteCollectionName != _activeCollectionName &&
                    Time.realtimeSinceStartup >= _nextCollectionRetry)
                {
                    _nextCollectionRetry = Time.realtimeSinceStartup + 1f;
                    tk2dSpriteCollectionData collection = FindCollection(spriteCollectionName);
                    if (collection != null && collection.spriteDefinitions != null && collection.spriteDefinitions.Length > 0)
                    {
                        _sprite.SetSprite(collection, Mathf.Clamp(spriteId, 0, collection.spriteDefinitions.Length - 1));
                        _activeCollectionName = spriteCollectionName;
                    }
                }
                if (spriteId >= 0 && _sprite.Collection != null && _sprite.Collection.spriteDefinitions != null &&
                    spriteId < _sprite.Collection.spriteDefinitions.Length)
                    _sprite.SetSprite(spriteId);
                _isDodgeRolling = isDodgeRolling;
                RefreshColor();
            }
        }

        public void SetDowned(bool downed)
        {
            _isDowned = downed;
            RefreshColor();
        }

        private void RefreshColor()
        {
            if (_sprite == null) return;
            _sprite.color = _isDowned
                ? new Color(_baseColor.r * 0.55f, _baseColor.g * 0.55f, _baseColor.b * 0.55f, _baseColor.a * 0.8f)
                : (_isDodgeRolling ? new Color(_baseColor.r, _baseColor.g, _baseColor.b, _baseColor.a * 0.82f) : _baseColor);
        }

        private void Update()
        {
            if (_sprite == null && Time.realtimeSinceStartup >= _nextVisualRetry)
            {
                _nextVisualRetry = Time.realtimeSinceStartup + 0.25f;
                TryCreateVisual();
            }
            if (!_hasTarget) return;

            float age = Mathf.Clamp(Time.realtimeSinceStartup - _lastSnapshotTime, 0f, 0.12f);
            Vector3 predicted = _targetPosition + new Vector3(_networkVelocity.x, _networkVelocity.y, 0f) * age;
            float distance = Vector3.Distance(transform.position, predicted);
            if (distance > 12f)
                transform.position = predicted;
            else
                transform.position = Vector3.Lerp(transform.position, predicted, 1f - Mathf.Exp(-14f * Time.deltaTime));
        }

        private static PlayerController GetLocalPlayer()
        {
            GameManager gameManager = Object.FindObjectOfType<GameManager>();
            return gameManager != null ? gameManager.PrimaryPlayer : null;
        }

        private static tk2dSpriteCollectionData FindCollection(string collectionName)
        {
            UnityEngine.Object[] collections = Resources.FindObjectsOfTypeAll(typeof(tk2dSpriteCollectionData));
            for (int i = 0; i < collections.Length; i++)
            {
                tk2dSpriteCollectionData collection = collections[i] as tk2dSpriteCollectionData;
                if (collection != null && collection.name == collectionName)
                    return collection;
            }
            return null;
        }
    }
}
