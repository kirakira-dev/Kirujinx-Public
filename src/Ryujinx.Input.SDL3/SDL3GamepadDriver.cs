using Ryujinx.Common.Logging;
using Ryujinx.SDL3.Common;
using System;
using System.Collections.Generic;
using System.Threading;
using SDL;
using System.Linq;
using static SDL.SDL3;

namespace Ryujinx.Input.SDL3
{
    public unsafe class SDL3GamepadDriver : IGamepadDriver
    {
        private readonly Dictionary<SDL_JoystickID, string> _gamepadsInstanceIdsMapping;
        private readonly Dictionary<SDL_JoystickID, string> _gamepadsIds;
        private readonly Lock _lock = new();

        public ReadOnlySpan<string> GamepadsIds
        {
            get
            {
                lock (_lock)
                {
                    return _gamepadsIds.Values.ToArray();
                }
            }
        }

        public string DriverName => "SDL3";

        public event Action<string> OnGamepadConnected;
        public event Action<string> OnGamepadDisconnected;

        public SDL3GamepadDriver()
        {
            _gamepadsInstanceIdsMapping = new Dictionary<SDL_JoystickID, string>();
            _gamepadsIds = [];

            SDL3Driver.Instance.Initialize();
            SDL3Driver.Instance.OnJoyStickConnected += HandleJoyStickConnected;
            SDL3Driver.Instance.OnJoystickDisconnected += HandleJoyStickDisconnected;
            SDL3Driver.Instance.OnJoyBatteryUpdated += HandleJoyBatteryUpdated;

            // Add already connected gamepads
            int joystickCount = 0;

            SDL_JoystickID* pJoystickInstanceIds = SDL_GetJoysticks(&joystickCount);

            for (int i = 0; i < joystickCount; i++)
            {
                HandleJoyStickConnected(pJoystickInstanceIds[i]);
            }
        }

        private unsafe static string SDLGuidToString(SDL_GUID guid)
        {
            string map = "0123456789abcdef";
            char[] guidBytes = new char[33];

            for (int i = 0; i < 16; i++) {
                byte c = guid.data[i];
                guidBytes[i * 2] = map[c >> 4];
                guidBytes[(i * 2) + 1] = map[c & 0x0f];
            }

            string strGuid = new(guidBytes);

            return $"{strGuid[4..6]}{strGuid[6..8]}{strGuid[2..4]}{strGuid[0..2]}-{strGuid[10..12]}{strGuid[8..10]}-{strGuid[12..16]}-{strGuid[16..20]}-{strGuid[20..32]}";

        }

        private unsafe string GenerateGamepadId(SDL_JoystickID joystickInstanceId)
        {
            SDL_GUID sdlGuid = SDL_GetJoystickGUIDForID(joystickInstanceId);
            string guidBytes = SDLGuidToString(sdlGuid);
            Guid guid = Guid.Parse(guidBytes);

            // Add a unique identifier to the start of the GUID in case of duplicates.

            if (guid == Guid.Empty)
            {
                return null;
            }

            // Remove the first 4 char of the guid (CRC part) to make it stable
            string guidString = $"0000{guid.ToString()[4..]}";

            string id;

            lock (_lock)
            {
                int guidIndex = 0;
                id = guidIndex + "-" + guidString;

                while (_gamepadsIds.ContainsValue(id))
                {
                    id = (++guidIndex) + "-" + guidString;
                }
            }

            return id;
        }

        private void HandleJoyStickDisconnected(SDL_JoystickID joystickInstanceId)
        {
            bool joyConPairDisconnected = false;

            if (!_gamepadsInstanceIdsMapping.Remove(joystickInstanceId, out string id))
                return;

            lock (_lock)
            {
                _gamepadsIds.Remove(joystickInstanceId);
                if (!SDL3JoyConPair.IsCombinable(_gamepadsIds))
                {
                    _gamepadsIds.Remove(GetInstanceIdFromId(SDL3JoyConPair.Id));
                    joyConPairDisconnected = true;
                }
            }

            OnGamepadDisconnected?.Invoke(id);
            if (joyConPairDisconnected)
            {
                OnGamepadDisconnected?.Invoke(SDL3JoyConPair.Id);
            }
        }

        private void HandleJoyStickConnected(SDL_JoystickID joystickInstanceId)
        {
            bool joyConPairConnected = false;

            if (SDL_IsGamepad(joystickInstanceId))
            {
                if (_gamepadsInstanceIdsMapping.ContainsKey(joystickInstanceId))
                {
                    // Sometimes a JoyStick connected event fires after the app starts even though it was connected before
                    // so it is rejected to avoid doubling the entries.
                    return;
                }

                string id = GenerateGamepadId(joystickInstanceId);

                if (id == null)
                {
                    return;
                }

                if (_gamepadsInstanceIdsMapping.TryAdd(joystickInstanceId, id))
                {
                    lock (_lock)
                    {

                        _gamepadsIds.Add(joystickInstanceId, id);

                        if (SDL3JoyConPair.IsCombinable(_gamepadsIds))
                        {
                            // TODO - It appears that you can only have one joy con pair connected at a time?
                            // This was also the behavior before SDL3
                            _gamepadsIds.Remove(GetInstanceIdFromId(SDL3JoyConPair.Id));
                            uint fakeInstanceID = uint.MaxValue;
                            while (!_gamepadsIds.TryAdd((SDL_JoystickID)fakeInstanceID, SDL3JoyConPair.Id))
                            {
                                fakeInstanceID--;
                            }
                            joyConPairConnected = true;
                        }
                    }

                    OnGamepadConnected?.Invoke(id);
                    if (joyConPairConnected)
                    {
                        OnGamepadConnected?.Invoke(SDL3JoyConPair.Id);
                    }
                }
            }
        }

        private void HandleJoyBatteryUpdated(SDL_JoystickID joystickInstanceId, SDL_PowerState powerLevel)
        {
            Logger.Info?.Print(LogClass.Hid,
                $"{SDL_GetGamepadNameForID(joystickInstanceId)} power level: {powerLevel}");
        }

        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
            {
                SDL3Driver.Instance.OnJoyStickConnected -= HandleJoyStickConnected;
                SDL3Driver.Instance.OnJoystickDisconnected -= HandleJoyStickDisconnected;

                // Simulate a full disconnect when disposing
                foreach (var gamepad in _gamepadsIds)
                {
                    OnGamepadDisconnected?.Invoke(gamepad.Value);
                }

                lock (_lock)
                {
                    _gamepadsIds.Clear();
                }

                SDL3Driver.Instance.Dispose();
            }
        }

        public void Dispose()
        {
            GC.SuppressFinalize(this);
            Dispose(true);
        }

        public SDL_JoystickID GetInstanceIdFromId(string id) {
            return _gamepadsInstanceIdsMapping.Where(e => e.Value == id).FirstOrDefault().Key;
        }

        public IGamepad GetGamepad(string id)
        {
            if (id == SDL3JoyConPair.Id)
            {
                lock (_lock)
                {
                    return SDL3JoyConPair.GetGamepad(_gamepadsIds);
                }
            }

            SDL_JoystickID instanceId = GetInstanceIdFromId(id);

            SDL_Gamepad* gamepadHandle = SDL_OpenGamepad(instanceId);

            if (gamepadHandle == null)
            {
                return null;
            }

            if (SDL_GetGamepadName(gamepadHandle).StartsWith(SDL3JoyCon.Prefix))
            {
                return new SDL3JoyCon(gamepadHandle, id);
            }

            return new SDL3Gamepad(gamepadHandle, id);
        }

        public IEnumerable<IGamepad> GetGamepads()
        {
            lock (_gamepadsIds)
            {
                foreach (var gamepad in _gamepadsIds)
                {
                    yield return GetGamepad(gamepad.Value);
                }
            }
        }
    }
}
