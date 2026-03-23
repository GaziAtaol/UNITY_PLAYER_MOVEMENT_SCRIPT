using UnityEngine;
using Unity.Netcode;

/// <summary>
/// Unity 6 + Netcode for GameObjects - Professional Character Movement
/// CharacterController tabanlı, owner-authoritative movement system
/// 
/// Özellikler:
/// - Smooth WASD hareket (acceleration/deceleration)
/// - Sprint mekanizması
/// - Precise jump + gravity (terminal velocity dahil)
/// - Coyote time + jump buffer
/// - Variable jump height
/// - Mouse look (yaw + pitch)
/// - Multiplayer sync (NetworkTransform ile)
/// - Zero drift guarantee (kendi kendine hareket yok)
/// - Cursor auto-unlock on focus loss
/// - OnLanded virtual hook (alt sınıflar için genişletilebilir)
/// - Debug logging sistemi
/// </summary>
[RequireComponent(typeof(CharacterController))]
public class PlayerMovementCC : NetworkBehaviour
{
    #region Inspector Fields
    
    [Header("━━━━━ MOVEMENT ━━━━━")]
    [SerializeField] private float walkSpeed = 5f;
    [Tooltip("Sprint hızı (Shift basılıyken)")]
    [SerializeField] private float sprintSpeed = 8f;
    
    [Header("━━━━━ SMOOTHING ━━━━━")]
    [Tooltip("Hızlanma oranı (ne kadar yüksekse o kadar hızlı max speed'e ulaşır)")]
    [SerializeField] private float acceleration = 12f;
    [Tooltip("Yavaşlama oranı (tuş bırakınca ne kadar hızlı durur)")]
    [SerializeField] private float deceleration = 18f;
    [Tooltip("Havadayken hareket kontrolü (0=yok, 1=tam kontrol)")]
    [Range(0f, 1f)]
    [SerializeField] private float airControl = 0.3f;
    [Tooltip("Anında dur mekanizması aktif mi? (arcade tarz)")]
    [SerializeField] private bool instantStop = false;
    
    [Header("━━━━━ LOOK ━━━━━")]
    [Tooltip("Mouse hassasiyeti")]
    [SerializeField] private float mouseSensitivity = 2f;
    [Tooltip("Camera pivot (pitch için - genelde Camera'nın parent'ı)")]
    [SerializeField] private Transform cameraPivot;
    [Tooltip("Aşağı bakma limiti (derece)")]
    [SerializeField] private float pitchMin = -80f;
    [Tooltip("Yukarı bakma limiti (derece)")]
    [SerializeField] private float pitchMax = 80f;
    
    [Header("━━━━━ JUMP & GRAVITY ━━━━━")]
    [Tooltip("Zıplama yüksekliği (metre)")]
    [SerializeField] private float jumpHeight = 2f;
    [Tooltip("Yerçekimi kuvveti (negatif değer)")]
    [SerializeField] private float gravity = -15f;
    [Tooltip("Yere yapışma kuvveti (rampalarda kaymaması için)")]
    [SerializeField] private float groundStickForce = -2f;
    [Tooltip("Terminal hız - maksimum düşme hızı (negatif değer)")]
    [SerializeField] private float terminalVelocity = -30f;
    [Tooltip("Coyote time - Kenardan düştükten sonra zıplama toleransı (saniye)")]
    [SerializeField] private float coyoteTime = 0.15f;
    [Tooltip("Jump buffer - Yere inmeden önce basılan jump toleransı (saniye)")]
    [SerializeField] private float jumpBufferTime = 0.2f;
    
    [Header("━━━━━ INPUT ━━━━━")]
    [Tooltip("Joystick drift önleme threshold")]
    [SerializeField] private float inputDeadzone = 0.12f;
    [SerializeField] private KeyCode sprintKey = KeyCode.LeftShift;
    [SerializeField] private KeyCode jumpKey = KeyCode.Space;
    
    [Header("━━━━━ DEBUG ━━━━━")]
    [SerializeField] private bool showDebugLogs = true;
    [SerializeField] private bool showDebugGizmos = true;
    
    #endregion
    
    #region Private Fields
    
    // Components
    private CharacterController characterController;
    
    // Movement State
    private Vector3 planarVelocity;      // XZ ekseni hız
    private float verticalVelocity;      // Y ekseni hız
    private Vector3 smoothInputDirection; // Smooth edilmiş input direction
    
    // Look State
    private float currentYaw;
    private float currentPitch;
    
    // Jump Mechanics
    private float coyoteTimeCounter;
    private float jumpBufferCounter;
    private bool wasGrounded;
    
    // Cached Input State
    private bool isSprinting;
    
    // Owner Control
    private bool isOwnerActive;
    
    #endregion
    
    #region Unity Lifecycle
    
    private void Awake()
    {
        characterController = GetComponent<CharacterController>();
        
        if (showDebugLogs)
            Log($"Awake - GameObject: {gameObject.name}");
    }
    
    public override void OnNetworkSpawn()
    {
        base.OnNetworkSpawn();
        
        if (showDebugLogs)
            Log($"OnNetworkSpawn - IsOwner: {IsOwner} | IsServer: {IsServer} | IsClient: {IsClient}");
        
        // ═══════════════════════════════════════════════════
        // KRİTİK: Sadece owner bu script'i çalıştırmalı
        // ═══════════════════════════════════════════════════
        if (!IsOwner)
        {
            enabled = false;
            
            if (showDebugLogs)
                Log("Script disabled - NOT OWNER");
            
            return;
        }
        
        InitializeOwner();
    }
    
    public override void OnNetworkDespawn()
    {
        base.OnNetworkDespawn();
        
        if (IsOwner)
            UnlockCursor();
    }
    
    private void OnApplicationFocus(bool hasFocus)
    {
        if (!IsOwner || !isOwnerActive) return;
        
        // Uygulama focus kaybedince cursor'u serbest bırak
        if (!hasFocus && Cursor.lockState == CursorLockMode.Locked)
            UnlockCursor();
    }
    
    private void UnlockCursor()
    {
        Cursor.lockState = CursorLockMode.None;
        Cursor.visible = true;
    }
    
    private void Update()
    {
        if (!IsOwner || !isOwnerActive) return;
        
        HandleLookInput();
        HandleMovementInput();
        HandleJumpMechanics();
        ApplyGravity();
        MoveCharacter();
        
        // Landing detection
        bool justLanded = !wasGrounded && characterController.isGrounded;
        if (justLanded) OnLanded();
        
        // Ground state tracking
        wasGrounded = characterController.isGrounded;
    }
    
    #endregion
    
    #region Initialization
    
    private void InitializeOwner()
    {
        // Cursor lock - BAŞLANGIÇTA KİLİTLEME (UI'a tıklayabilmek için)
        // İlk mouse click ile kilitlenecek
        UnlockCursor();
        
        // Rotation başlangıç değerleri
        currentYaw = transform.eulerAngles.y;
        currentPitch = 0f;
        
        // Velocity reset (spawn drift önleme)
        planarVelocity = Vector3.zero;
        verticalVelocity = 0f;
        smoothInputDirection = Vector3.zero;
        
        // Jump counters reset
        coyoteTimeCounter = 0f;
        jumpBufferCounter = 0f;
        
        isOwnerActive = true;
        
        if (showDebugLogs)
            Log("OWNER INITIALIZED - Movement active");
    }
    
    #endregion
    
    #region Look Input
    
    private void HandleLookInput()
    {
        // İlk mouse click ile cursor'u kilitle
        if (Cursor.lockState != CursorLockMode.Locked)
        {
            if (Input.GetMouseButtonDown(0) || Input.GetMouseButtonDown(1))
            {
                Cursor.lockState = CursorLockMode.Locked;
                Cursor.visible = false;
            }
            return; // Kilitli değilse look input okuma
        }
        
        // ESC ile unlock
        if (Input.GetKeyDown(KeyCode.Escape))
        {
            UnlockCursor();
            return;
        }
        
        // Mouse input
        float mouseX = Input.GetAxisRaw("Mouse X");
        float mouseY = Input.GetAxisRaw("Mouse Y");
        
        // ═══ YAW (Horizontal - Character rotation) ═══
        currentYaw += mouseX * mouseSensitivity;
        transform.rotation = Quaternion.Euler(0f, currentYaw, 0f);
        
        // ═══ PITCH (Vertical - Camera rotation) ═══
        if (cameraPivot != null)
        {
            currentPitch -= mouseY * mouseSensitivity;
            currentPitch = Mathf.Clamp(currentPitch, pitchMin, pitchMax);
            cameraPivot.localRotation = Quaternion.Euler(currentPitch, 0f, 0f);
        }
    }
    
    #endregion
    
    #region Movement Input
    
    private void HandleMovementInput()
    {
        // ═══════════════════════════════════════════════════
        // INPUT OKUMA (Drift-safe)
        // ═══════════════════════════════════════════════════
        float horizontal = Input.GetAxisRaw("Horizontal");
        float vertical = Input.GetAxisRaw("Vertical");
        
        // Deadzone uygula (controller drift önleme)
        if (Mathf.Abs(horizontal) < inputDeadzone) horizontal = 0f;
        if (Mathf.Abs(vertical) < inputDeadzone) vertical = 0f;
        
        // ═══════════════════════════════════════════════════
        // WORLD SPACE DIRECTION HESAPLAMA
        // ═══════════════════════════════════════════════════
        Vector3 inputDirection = Vector3.zero;
        
        if (horizontal != 0f || vertical != 0f)
        {
            // Character'in forward/right vektörlerini kullan
            Vector3 forward = transform.forward;
            Vector3 right = transform.right;
            
            // Input direction oluştur
            inputDirection = (right * horizontal + forward * vertical);
            
            // KRİTİK: Normalize et (diagonal movement hız avantajı olmaması için)
            inputDirection.Normalize();
        }
        
        // ═══════════════════════════════════════════════════
        // SMOOTH INPUT (opsiyonel - daha organik his için)
        // ═══════════════════════════════════════════════════
        // smoothInputDirection = Vector3.Lerp(
        //     smoothInputDirection, 
        //     inputDirection, 
        //     10f * Time.deltaTime
        // );
        
        // Şimdilik direct kullan (daha responsive)
        smoothInputDirection = inputDirection;
        
        // ═══════════════════════════════════════════════════
        // SPEED HESAPLAMA
        // ═══════════════════════════════════════════════════
        isSprinting = Input.GetKey(sprintKey);
        float targetSpeed = isSprinting ? sprintSpeed : walkSpeed;
        
        // ═══════════════════════════════════════════════════
        // TARGET VELOCITY
        // ═══════════════════════════════════════════════════
        Vector3 targetVelocity = smoothInputDirection * targetSpeed;
        
        // ═══════════════════════════════════════════════════
        // KRİTİK: Input yoksa target = ZERO
        // Bu "kendi kendine hareket" bug'ını önler
        // ═══════════════════════════════════════════════════
        if (smoothInputDirection.sqrMagnitude < 0.01f)
        {
            targetVelocity = Vector3.zero;
        }
        
        // ═══════════════════════════════════════════════════
        // SMOOTH VELOCITY TRANSITION
        // ═══════════════════════════════════════════════════
        float smoothRate;
        
        if (instantStop && targetVelocity.sqrMagnitude < 0.01f)
        {
            // Anında dur (arcade style)
            planarVelocity = Vector3.zero;
        }
        else
        {
            // Acceleration veya Deceleration seç
            smoothRate = (targetVelocity.sqrMagnitude > 0.01f) ? acceleration : deceleration;
            
            // Havadayken air control uygula
            if (!characterController.isGrounded)
            {
                smoothRate *= airControl;
            }
            
            // Velocity'yi smooth transition ile güncelle
            planarVelocity = Vector3.MoveTowards(
                planarVelocity,
                targetVelocity,
                smoothRate * Time.deltaTime
            );
        }
        
        // ═══════════════════════════════════════════════════
        // DEBUG LOG (drift tespiti)
        // ═══════════════════════════════════════════════════
        if (showDebugLogs && smoothInputDirection.sqrMagnitude < 0.01f && planarVelocity.sqrMagnitude > 0.05f)
        {
            LogWarning($"DRIFT DETECTED! No input but velocity: {planarVelocity.magnitude:F3}");
        }
    }
    
    #endregion
    
    #region Jump Mechanics
    
    private void HandleJumpMechanics()
    {
        // ═══════════════════════════════════════════════════
        // COYOTE TIME (kenardan düştükten sonra jump toleransı)
        // ═══════════════════════════════════════════════════
        if (characterController.isGrounded)
        {
            coyoteTimeCounter = coyoteTime;
        }
        else
        {
            coyoteTimeCounter -= Time.deltaTime;
        }
        
        // ═══════════════════════════════════════════════════
        // JUMP BUFFER (yere inmeden önce basılan jump)
        // ═══════════════════════════════════════════════════
        if (Input.GetKeyDown(jumpKey))
        {
            jumpBufferCounter = jumpBufferTime;
        }
        else
        {
            jumpBufferCounter -= Time.deltaTime;
        }
        
        // ═══════════════════════════════════════════════════
        // JUMP EXECUTION
        // ═══════════════════════════════════════════════════
        bool canJump = coyoteTimeCounter > 0f;
        bool wantsToJump = jumpBufferCounter > 0f;
        
        if (canJump && wantsToJump)
        {
            PerformJump();
            
            // Counters reset
            jumpBufferCounter = 0f;
            coyoteTimeCounter = 0f;
        }
        
        // ═══════════════════════════════════════════════════
        // VARIABLE JUMP HEIGHT (tuşu erken bırakınca daha kısa zıplar)
        // ═══════════════════════════════════════════════════
        if (Input.GetKeyUp(jumpKey) && verticalVelocity > 0f)
        {
            verticalVelocity *= 0.5f;
        }
    }
    
    private void PerformJump()
    {
        // Physics formula: v = sqrt(2 * h * -g)
        verticalVelocity = Mathf.Sqrt(2f * jumpHeight * -gravity);
        
        if (showDebugLogs)
            Log($"JUMP! Height: {jumpHeight}m | Initial velocity: {verticalVelocity:F2}");
    }
    
    /// <summary>
    /// Called the frame the character touches the ground after being airborne.
    /// Override in a subclass to add landing effects, sounds, or camera shake.
    /// </summary>
    protected virtual void OnLanded()
    {
        if (showDebugLogs)
            Log("Landed!");
    }
    
    #endregion
    
    #region Gravity
    
    private void ApplyGravity()
    {
        if (characterController.isGrounded)
        {
            // Yerdeyken negatif küçük bir değer (rampalarda kaymaması için)
            if (verticalVelocity < 0f)
            {
                verticalVelocity = groundStickForce;
            }
        }
        else
        {
            // Havadayken gravity uygula
            verticalVelocity += gravity * Time.deltaTime;
            
            // Terminal velocity - maksimum düşme hızını sınırla
            verticalVelocity = Mathf.Max(verticalVelocity, terminalVelocity);
        }
    }
    
    #endregion
    
    #region Character Movement
    
    private void MoveCharacter()
    {
        // ═══════════════════════════════════════════════════
        // FINAL VELOCITY = Planar (XZ) + Vertical (Y)
        // ═══════════════════════════════════════════════════
        Vector3 finalVelocity = new Vector3(
            planarVelocity.x,
            verticalVelocity,
            planarVelocity.z
        );
        
        // ═══════════════════════════════════════════════════
        // CharacterController.Move() - deltaTime dahil
        // ═══════════════════════════════════════════════════
        characterController.Move(finalVelocity * Time.deltaTime);
    }
    
    #endregion
    
    #region Debug & Utilities
    
    private void Log(string message)
    {
        Debug.Log($"<color=cyan>[PlayerMovement]</color> {message}", this);
    }
    
    private void LogWarning(string message)
    {
        Debug.LogWarning($"<color=yellow>[PlayerMovement]</color> {message}", this);
    }
    
    private void OnDrawGizmos()
    {
        if (!showDebugGizmos || !Application.isPlaying) return;
        if (!IsOwner) return;
        
        // Velocity direction göster
        Gizmos.color = Color.green;
        Vector3 velocityVisualization = new Vector3(planarVelocity.x, 0f, planarVelocity.z);
        Gizmos.DrawRay(transform.position + Vector3.up, velocityVisualization);
        
        // Input direction göster
        Gizmos.color = Color.yellow;
        Gizmos.DrawRay(transform.position + Vector3.up * 1.5f, smoothInputDirection * 2f);
    }
    
    #endregion
    
    #region Public Getters (ileride UI veya diğer sistemler için)
    
    public bool IsGrounded => characterController.isGrounded;
    public bool IsMoving => planarVelocity.sqrMagnitude > 0.1f;
    public bool IsSprinting => isSprinting && IsMoving;
    public float CurrentSpeed => planarVelocity.magnitude;
    public Vector3 Velocity => new Vector3(planarVelocity.x, verticalVelocity, planarVelocity.z);
    
    #endregion
}
