# Changelog

## 0.9.0

- Added party-wide room transitions and three-player native camera bounds.
- Registered all three controllers for room logic and enemy targeting.
- Synchronized enemies across active rooms and activated nearby teammate revives.
- Added host-authoritative ground drops, pickup claims and shared money/keys.
- Reject mismatched generated maps caused by different mods or unlock state.
- Added measured round-trip latency and synchronized gun aim direction.
- Protocol version 13; every participant must use 0.9.0.

## 0.8.3

- Added the optional Isaac-style three-player corner HUD and F8 toggle.

## 0.8.2

- Added shared run seeds, map fingerprints, safe placement and black-screen fallbacks.

## 0.8.1

- Protected the camera during floor loads and retried late room placement.

## 0.8.0

- Replaced visual-only avatars with physical network-controlled PlayerControllers.
- Added host-authoritative interactions, room state, shared resources and modded equipment identity.
