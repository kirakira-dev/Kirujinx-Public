using Ryujinx.Common.Configuration.Hid;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using SDL;
using static SDL.SDL3;

namespace Ryujinx.Input.SDL3
{
    internal class SDL3JoyConPair(IGamepad left, IGamepad right) : IGamepad
    {
        public GamepadFeaturesFlag Features => (left?.Features ?? GamepadFeaturesFlag.None) |
                                               (right?.Features ?? GamepadFeaturesFlag.None);

        public const string Id = "JoyConPair";
        string IGamepad.Id => Id;

        public string Name => "* Nintendo Switch Joy-Con (L/R)";
        public bool IsConnected => left is { IsConnected: true } && right is { IsConnected: true };

        public void Dispose()
        {
            left?.Dispose();
            right?.Dispose();
        }

        public GamepadStateSnapshot GetMappedStateSnapshot()
        {
            return GetStateSnapshot();
        }

        public Vector3 GetMotionData(MotionInputId inputId)
        {
            return inputId switch
            {
                MotionInputId.Accelerometer or
                    MotionInputId.Gyroscope => left.GetMotionData(inputId),
                MotionInputId.SecondAccelerometer => right.GetMotionData(MotionInputId.Accelerometer),
                MotionInputId.SecondGyroscope => right.GetMotionData(MotionInputId.Gyroscope),
                _ => Vector3.Zero
            };
        }

        public GamepadStateSnapshot GetStateSnapshot()
        {
            return IGamepad.GetStateSnapshot(this);
        }

        public (float, float) GetStick(StickInputId inputId)
        {
            return inputId switch
            {
                StickInputId.Left => left.GetStick(StickInputId.Left),
                StickInputId.Right => right.GetStick(StickInputId.Right),
                _ => (0, 0)
            };
        }

        public bool IsPressed(GamepadButtonInputId inputId)
        {
            return left.IsPressed(inputId) || right.IsPressed(inputId);
        }

        public void Rumble(float lowFrequency, float highFrequency, uint durationMs)
        {
            if (lowFrequency != 0)
            {
                right.Rumble(lowFrequency, lowFrequency, durationMs);
            }

            if (highFrequency != 0)
            {
                left.Rumble(highFrequency, highFrequency, durationMs);
            }

            if (lowFrequency == 0 && highFrequency == 0)
            {
                left.Rumble(0, 0, durationMs);
                right.Rumble(0, 0, durationMs);
            }
        }

        public void SetConfiguration(InputConfig configuration)
        {
            left.SetConfiguration(configuration);
            right.SetConfiguration(configuration);
        }

        public void SetLed(uint packedRgb)
        {
        }

        public void SetTriggerThreshold(float triggerThreshold)
        {
            left.SetTriggerThreshold(triggerThreshold);
            right.SetTriggerThreshold(triggerThreshold);
        }

        public static bool IsCombinable(Dictionary<SDL_JoystickID, string> gamepadsIds)
        {
            (int leftIndex, int rightIndex) = DetectJoyConPair(gamepadsIds);
            return leftIndex >= 0 && rightIndex >= 0;
        }

        private static (int leftIndex, int rightIndex) DetectJoyConPair(Dictionary<SDL_JoystickID, string> gamepadsIds)
        {
            Dictionary<string, SDL_JoystickID> gamepadNames = gamepadsIds
                .Where(gamepadId => gamepadId.Value != Id && SDL_GetGamepadNameForID(gamepadId.Key) is SDL3JoyCon.LeftName or SDL3JoyCon.RightName)
                .Select(gamepad => (SDL_GetGamepadNameForID(gamepad.Key), gamepad.Key))
                .ToDictionary();
            SDL_JoystickID idx;
            int leftIndex = gamepadNames.TryGetValue(SDL3JoyCon.LeftName, out idx) ? (int)idx : -1;
            int rightIndex = gamepadNames.TryGetValue(SDL3JoyCon.RightName, out idx) ? (int)idx : -1;

            return (leftIndex, rightIndex);
        }

        public unsafe static IGamepad GetGamepad(Dictionary<SDL_JoystickID, string> gamepadsIds)
        {
            (int leftIndex, int rightIndex) = DetectJoyConPair(gamepadsIds);

            if (leftIndex <= 0 || rightIndex <= 0)
            {
                return null;
            }

            SDL_Gamepad* leftGamepadHandle = SDL_OpenGamepad((SDL_JoystickID)leftIndex);
            SDL_Gamepad* rightGamepadHandle = SDL_OpenGamepad((SDL_JoystickID)rightIndex);

            if (leftGamepadHandle == null || rightGamepadHandle == null)
            {
                return null;
            }

            return new SDL3JoyConPair(new SDL3JoyCon(leftGamepadHandle, gamepadsIds[(SDL_JoystickID)leftIndex]),
                new SDL3JoyCon(rightGamepadHandle, gamepadsIds[(SDL_JoystickID)rightIndex]));
        }
    }
}
