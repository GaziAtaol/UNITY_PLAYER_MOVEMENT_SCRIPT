# Unity Player Movement Script

A professional, multiplayer-ready **CharacterController**-based player movement system for **Unity 6** using **Netcode for GameObjects (NGO)**. The script is owner-authoritative, meaning only the owning client simulates movement — other clients simply receive the final transform via `NetworkTransform`.

---

## Features

| Feature | Details |
|---|---|
| **WASD / Joystick movement** | Smooth acceleration & deceleration |
| **Sprint** | Configurable sprint speed (Left Shift by default) |
| **Jump** | Physics-based jump height formula |
| **Coyote time** | Jump tolerance after walking off a ledge |
| **Jump buffer** | Pre-land jump input buffering |
| **Variable jump height** | Release jump key early for a shorter jump |
| **Terminal velocity** | Caps maximum fall speed |
| **Mouse look** | Yaw (character) + Pitch (camera pivot) |
| **Air control** | Configurable in-air lateral movement factor |
| **Instant stop mode** | Optional arcade-style instant stop on input release |
| **Cursor management** | Click to lock, Escape to unlock, auto-unlock on focus loss |
| **OnLanded hook** | Virtual method to trigger landing effects/sounds |
| **Zero-drift guarantee** | Velocity is forced to zero when there is no input |
| **Multiplayer safe** | Script disables itself on non-owner clients |
| **Debug tools** | Colour-coded console logs + Scene-view Gizmos |

---

## Requirements

- **Unity 6** (6000.x or later)
- **Netcode for GameObjects** package (com.unity.netcode.gameobjects)
- The legacy **Input Manager** (Edit → Project Settings → Input Manager). The new Input System is **not** required.

---

## Setup

### 1. Player Prefab

1. Create a GameObject that will represent the player.
2. Add a **CharacterController** component (the script requires it — `[RequireComponent]` ensures it is always present).
3. Add the **PlayerMovementCC** script.
4. Add a **NetworkObject** component.
5. Add a **NetworkTransform** component and set it to **Owner Authoritative** mode so the owning client's position is replicated to all others automatically.

### 2. Camera Pivot

The script separates horizontal rotation (the whole player body) from vertical rotation (the camera only). To set this up:

```
Player (PlayerMovementCC, CharacterController, NetworkObject, NetworkTransform)
└── CameraPivot  ← assign this Transform to the "Camera Pivot" field
    └── Main Camera
```

- Create an **empty GameObject** as a child of the player called `CameraPivot`.
- Place the **Main Camera** as a child of `CameraPivot`.
- Drag `CameraPivot` into the **Camera Pivot** field in the Inspector.

> If Camera Pivot is left empty the horizontal (yaw) rotation still works; vertical (pitch) look is simply skipped.

### 3. Register the Prefab

Add the player prefab to the **NetworkManager → Network Prefabs** list and assign it as the **Player Prefab**.

---

## Inspector Parameters

### Movement

| Field | Default | Description |
|---|---|---|
| Walk Speed | `5` | Base movement speed in m/s |
| Sprint Speed | `8` | Speed while the sprint key is held |

### Smoothing

| Field | Default | Description |
|---|---|---|
| Acceleration | `12` | How quickly velocity ramps up to the target speed |
| Deceleration | `18` | How quickly velocity ramps down to zero after input is released |
| Air Control | `0.3` | Lateral control multiplier while airborne (0 = none, 1 = full) |
| Instant Stop | `false` | When enabled, lateral velocity snaps to zero the moment input is released (arcade feel) |

### Look

| Field | Default | Description |
|---|---|---|
| Mouse Sensitivity | `2` | Mouse look sensitivity |
| Camera Pivot | —  | Transform rotated on the pitch (X) axis for vertical look |
| Pitch Min | `-80` | Maximum downward look angle in degrees |
| Pitch Max | `80` | Maximum upward look angle in degrees |

### Jump & Gravity

| Field | Default | Description |
|---|---|---|
| Jump Height | `2` | Desired apex height in metres (used in physics formula) |
| Gravity | `-15` | Downward acceleration in m/s² |
| Ground Stick Force | `-2` | Small negative force applied while grounded to keep the controller on ramps |
| Terminal Velocity | `-30` | Maximum fall speed (caps `verticalVelocity` floor) |
| Coyote Time | `0.15` | Seconds after walking off a ledge during which a jump is still allowed |
| Jump Buffer Time | `0.2` | Seconds before landing during which a pre-pressed jump is remembered |

### Input

| Field | Default | Description |
|---|---|---|
| Input Deadzone | `0.12` | Raw axis values below this threshold are treated as zero (prevents controller drift) |
| Sprint Key | `LeftShift` | Key that activates sprint |
| Jump Key | `Space` | Key that triggers a jump |

### Debug

| Field | Default | Description |
|---|---|---|
| Show Debug Logs | `true` | Prints colour-coded messages to the Console |
| Show Debug Gizmos | `true` | Draws velocity and input-direction arrows in the Scene view during Play mode |

---

## Controls

| Input | Action |
|---|---|
| **W / A / S / D** or **Left Stick** | Move |
| **Left Shift** | Sprint |
| **Space** | Jump |
| **Mouse** | Look |
| **Left / Right Mouse Button** | Lock cursor |
| **Escape** | Unlock cursor |

---

## Public API

Other scripts can read the following properties at runtime:

```csharp
bool   IsGrounded   // true when the CharacterController reports ground contact
bool   IsMoving     // true when lateral speed > 0.1 m/s
bool   IsSprinting  // true when sprint key held and the player is moving
float  CurrentSpeed // lateral speed in m/s
Vector3 Velocity    // full 3-D velocity (XZ from planar, Y from vertical)
```

---

## Extending via Subclass

### Landing Effects

Override `OnLanded` to hook in sounds, camera shake, particle effects, etc.:

```csharp
public class MyPlayer : PlayerMovementCC
{
    [SerializeField] private AudioClip landSound;

    protected override void OnLanded()
    {
        base.OnLanded(); // keeps the debug log
        AudioSource.PlayClipAtPoint(landSound, transform.position);
    }
}
```

Replace the **PlayerMovementCC** component on the prefab with **MyPlayer** — no other changes needed.

---

## Architecture Notes

```
Update()
 ├── HandleLookInput()       — yaw (body) + pitch (camera pivot)
 ├── HandleMovementInput()   — reads WASD, applies deadzone, smooths velocity
 ├── HandleJumpMechanics()   — coyote time, jump buffer, variable height
 ├── ApplyGravity()          — gravity accumulation + terminal velocity clamp
 ├── MoveCharacter()         — single CharacterController.Move() call
 ├── OnLanded() (if just landed)
 └── wasGrounded update
```

**Multiplayer flow:**  
`OnNetworkSpawn` → if not owner, `enabled = false`. The owner runs the full `Update` loop; `NetworkTransform` (set to Owner Auth) replicates the resulting transform to all other clients automatically.

---

## Troubleshooting

| Symptom | Solution |
|---|---|
| Player doesn't move | Ensure this GameObject has a **NetworkObject** and is spawned through NetworkManager |
| Camera doesn't rotate vertically | Assign the **Camera Pivot** field in the Inspector |
| Player moves by itself | Check the scene for other scripts writing to `transform.position`. The drift detection log will warn in the Console. |
| Jump feels floaty | Increase **Gravity** (make it more negative) or decrease **Jump Height** |
| Cursor stays locked after Alt-Tab | This is handled automatically via `OnApplicationFocus` |
| Non-owner player jitters | Ensure **NetworkTransform** is set to **Owner Authoritative** interpolation mode |

---

## License

This project is open-source. Feel free to use it in personal and commercial Unity projects.
