package com.xenoversepad.controller

import android.annotation.SuppressLint
import android.content.Intent
import android.content.res.ColorStateList
import android.graphics.Color
import android.os.Bundle
import android.os.SystemClock
import android.provider.Settings
import android.text.Editable
import android.text.TextWatcher
import android.util.Log
import android.view.MotionEvent
import android.view.View
import android.view.ViewConfiguration
import android.view.animation.AccelerateInterpolator
import android.view.animation.DecelerateInterpolator
import android.widget.Button
import android.widget.EditText
import android.widget.ImageView
import android.widget.TextView
import android.widget.Toast
import androidx.activity.enableEdgeToEdge
import androidx.annotation.DrawableRes
import androidx.appcompat.app.AlertDialog
import androidx.appcompat.app.AppCompatActivity
import androidx.core.view.ViewCompat
import androidx.core.view.WindowInsetsCompat
import androidx.core.widget.ImageViewCompat
import androidx.lifecycle.lifecycleScope
import kotlinx.coroutines.Job
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.collectLatest
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import kotlin.math.abs

class MainActivity : AppCompatActivity() {

    companion object {
        private const val TAG = "MainActivity"
        private const val COMBO_HOLD_MS = 100L
        private const val GUIDE_LONG_PRESS_MS = 1000L
        private const val SWIPE_MIN_DISTANCE_DP = 36f
        private const val SWIPE_MIN_VELOCITY_PX_PER_MS = 0.15f
    }

    // ── Views ───────────────────────────────────────────────────────────
    private lateinit var editIp: EditText
    private lateinit var btnConnect: Button
    private lateinit var imgConnectionStatus: ImageView
    private lateinit var tvConnectionText: TextView
    private lateinit var viewTouchpad: View
    private lateinit var btnXboxLogo: View
    private var statusDot: View? = null
    private var tvStatus: TextView? = null

    // Shoulders & triggers
    private lateinit var btnLB: Button
    private lateinit var btnRB: Button
    private lateinit var triggerLT: TriggerView
    private lateinit var triggerRT: TriggerView

    // Center
    private lateinit var btnBack: Button
    private lateinit var btnStart: Button
    private lateinit var btnProfile: Button
    private lateinit var tvComboInfo: TextView

    // Sticks
    private lateinit var joystickLeft: JoystickView
    private lateinit var joystickRight: JoystickView

    // Face buttons
    private lateinit var btnA: Button
    private lateinit var btnB: Button
    private lateinit var btnX: Button
    private lateinit var btnY: Button

    // D-Pad
    private lateinit var btnDpadUp: Button
    private lateinit var btnDpadDown: Button
    private lateinit var btnDpadLeft: Button
    private lateinit var btnDpadRight: Button

    // ── State ───────────────────────────────────────────────────────────
    private var networkManager: NetworkManager? = null
    private var hapticManager: HapticManager? = null
    private var inputLoopJob: Job? = null
    private var transportJob: Job? = null
    private var guideLongPressJob: Job? = null
    private var connected = false
    private var displayedTransport: NetworkManager.Transport? = null

    // Tracks whether the user was blocked by the USB Debugging dialog and went to Settings
    private var waitingForUsbDebuggingSettings = false

    @Volatile private var currentLX: Short = 0
    @Volatile private var currentLY: Short = 0
    @Volatile private var currentRX: Short = 0
    @Volatile private var currentRY: Short = 0
    @Volatile private var currentButtons: Int = 0
    @Volatile private var currentLT: Int = 0
    @Volatile private var currentRT: Int = 0

    // Profile: TYPE_A or TYPE_C
    private var profileIsTypeA = true

    // ── Lifecycle ───────────────────────────────────────────────────────

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        enableEdgeToEdge()
        setContentView(R.layout.activity_main)
        ViewCompat.setOnApplyWindowInsetsListener(findViewById(R.id.main)) { v, insets ->
            val systemBars = insets.getInsets(WindowInsetsCompat.Type.systemBars())
            v.setPadding(systemBars.left, systemBars.top, systemBars.right, systemBars.bottom)
            insets
        }

        // Initialize HapticManager immediately so button/joystick/trigger touches vibrate even before connecting
        hapticManager = HapticManager(this)

        bindViews()
        setupConnection()
        setupTouchpad()
        setupXboxLogoButton()
        setupJoysticks()
        setupFaceButtons()
        setupDPad()
        setupShoulders()
        setupTriggers()
        setupCenterButtons()
        setupProfileToggle()
    }

    override fun onResume() {
        super.onResume()
        // Auto-recheck if the user was redirected to Developer Options to enable USB Debugging
        if (waitingForUsbDebuggingSettings) {
            if (isUsbDebuggingEnabled()) {
                waitingForUsbDebuggingSettings = false
                Toast.makeText(
                    this,
                    "USB Debugging Enabled! Ready to connect.",
                    Toast.LENGTH_SHORT
                ).show()
                if (!connected) {
                    connect()
                }
            }
        }
    }

    override fun onDestroy() {
        guideLongPressJob?.cancel()
        imgConnectionStatus.animate().cancel()
        tvConnectionText.animate().cancel()
        btnXboxLogo.animate().cancel()
        disconnect()
        hapticManager?.release()
        hapticManager = null
        super.onDestroy()
    }

    // ── View binding ────────────────────────────────────────────────────

    private fun bindViews() {
        editIp              = findViewById(R.id.editIpAddress)
        btnConnect          = findViewById(R.id.btnConnect)
        imgConnectionStatus = findViewById(R.id.imgConnectionStatus)
        tvConnectionText    = findViewById(R.id.tvConnectionText)
        viewTouchpad        = findViewById(R.id.viewTouchpad)
        btnXboxLogo         = findViewById(R.id.btnXboxLogo)
        statusDot           = findViewById(R.id.statusDot)
        tvStatus            = findViewById(R.id.tvStatus)

        btnLB        = findViewById(R.id.btnLB)
        btnRB        = findViewById(R.id.btnRB)
        triggerLT    = findViewById(R.id.triggerLT)
        triggerRT    = findViewById(R.id.triggerRT)

        btnBack      = findViewById(R.id.btnBack)
        btnStart     = findViewById(R.id.btnStart)
        btnProfile   = findViewById(R.id.btnProfile)
        tvComboInfo  = findViewById(R.id.tvComboInfo)

        joystickLeft  = findViewById(R.id.joystickLeft)
        joystickRight = findViewById(R.id.joystickRight)

        btnA = findViewById(R.id.btnA)
        btnB = findViewById(R.id.btnB)
        btnX = findViewById(R.id.btnX)
        btnY = findViewById(R.id.btnY)

        btnDpadUp    = findViewById(R.id.btnDpadUp)
        btnDpadDown  = findViewById(R.id.btnDpadDown)
        btnDpadLeft  = findViewById(R.id.btnDpadLeft)
        btnDpadRight = findViewById(R.id.btnDpadRight)

        // Set Xbox Series X trigger accent colours (Xbox Green & Xbox Blue)
        triggerLT.fillColor = Color.parseColor("#107C10")
        triggerRT.fillColor = Color.parseColor("#0078D7")

        // Dynamically propagate IP text changes to NetworkManager
        editIp.addTextChangedListener(object : TextWatcher {
            override fun beforeTextChanged(s: CharSequence?, start: Int, count: Int, after: Int) {}
            override fun onTextChanged(s: CharSequence?, start: Int, before: Int, count: Int) {
                val ip = s?.toString()?.trim() ?: ""
                if (ip.isNotEmpty()) {
                    networkManager?.updateFallbackIp(ip)
                }
            }
            override fun afterTextChanged(s: Editable?) {}
        })
    }

    // ── 1. Top Space Touchpad (Swipes for Vanish/Grab, Tap for View) ────

    @SuppressLint("ClickableViewAccessibility")
    private fun setupTouchpad() {
        val density = resources.displayMetrics.density
        val minSwipeDistancePx = SWIPE_MIN_DISTANCE_DP * density
        val touchSlopPx = ViewConfiguration.get(this).scaledTouchSlop.toFloat()

        var downX = 0f
        var downY = 0f
        var downTimeMs = 0L
        var maxMoveDistance = 0f

        viewTouchpad.setOnTouchListener { v, event ->
            when (event.actionMasked) {
                MotionEvent.ACTION_DOWN -> {
                    downX = event.x
                    downY = event.y
                    downTimeMs = SystemClock.elapsedRealtime()
                    maxMoveDistance = 0f
                    true
                }
                MotionEvent.ACTION_MOVE -> {
                    val dx = event.x - downX
                    val dy = event.y - downY
                    val dist = abs(dx).coerceAtLeast(abs(dy))
                    if (dist > maxMoveDistance) {
                        maxMoveDistance = dist
                    }
                    true
                }
                MotionEvent.ACTION_UP -> {
                    val dx = event.x - downX
                    val dy = event.y - downY
                    val dtMs = (SystemClock.elapsedRealtime() - downTimeMs).coerceAtLeast(1L)
                    val velocityX = abs(dx) / dtMs

                    val isHorizontalSwipe = abs(dx) >= minSwipeDistancePx &&
                        abs(dx) > abs(dy) * 1.25f &&
                        velocityX >= SWIPE_MIN_VELOCITY_PX_PER_MS

                    if (isHorizontalSwipe) {
                        if (dx < 0f) {
                            // Swipe Left -> "Vanish" combo (LB + A for 100ms)
                            Log.i(TAG, "Touchpad Swipe Left → Vanish combo (LB+A)")
                            hapticManager?.playTap(220, 35L, v)
                            val vanishMask = (1 shl GamepadProtocol.BTN_LB) or (1 shl GamepadProtocol.BTN_A)
                            triggerTemporaryCombo(vanishMask, COMBO_HOLD_MS)
                        } else {
                            // Swipe Right -> "Grab" combo (LB + B for 100ms)
                            Log.i(TAG, "Touchpad Swipe Right → Grab combo (LB+B)")
                            hapticManager?.playTap(220, 35L, v)
                            val grabMask = (1 shl GamepadProtocol.BTN_LB) or (1 shl GamepadProtocol.BTN_B)
                            triggerTemporaryCombo(grabMask, COMBO_HOLD_MS)
                        }
                    } else if (maxMoveDistance <= touchSlopPx * 1.5f) {
                        // Simple Tap -> simulate pressing BTN_BACK (View button)
                        Log.i(TAG, "Touchpad Tap → BTN_BACK (View)")
                        hapticManager?.playTap(160, 22L, v)
                        triggerTemporaryCombo(1 shl GamepadProtocol.BTN_BACK, COMBO_HOLD_MS)
                    }
                    true
                }
                MotionEvent.ACTION_CANCEL -> true
                else -> false
            }
        }
    }

    /**
     * Set [mask] bits in [currentButtons], fire an immediate packet,
     * hold for [durationMs], then clear those bits and fire again.
     */
    private fun triggerTemporaryCombo(mask: Int, durationMs: Long) {
        lifecycleScope.launch {
            synchronized(this@MainActivity) {
                currentButtons = currentButtons or mask
            }
            sendImmediate()
            delay(durationMs)
            synchronized(this@MainActivity) {
                currentButtons = currentButtons and mask.inv()
            }
            sendImmediate()
        }
    }

    // ── 2. Xbox Guide Button (Short Press: Haptic Test, Long Press 1s: Toggle Connect) ──

    @SuppressLint("ClickableViewAccessibility")
    private fun setupXboxLogoButton() {
        var longPressHandled = false

        btnXboxLogo.setOnTouchListener { _, event ->
            when (event.actionMasked) {
                MotionEvent.ACTION_DOWN -> {
                    longPressHandled = false

                    // Visual feedback: scale down to 0.9x and add a subtle glow/alpha shift
                    btnXboxLogo.animate().cancel()
                    btnXboxLogo.animate()
                        .scaleX(0.9f)
                        .scaleY(0.9f)
                        .alpha(0.75f)
                        .setDuration(90L)
                        .start()

                    // Start 1-second hold timer to toggle connection state
                    guideLongPressJob?.cancel()
                    guideLongPressJob = lifecycleScope.launch {
                        delay(GUIDE_LONG_PRESS_MS)
                        longPressHandled = true
                        Log.i(TAG, "Xbox Guide Button Long Press (1s) → Toggling connection")
                        hapticManager?.playRumble(255, 255)
                        if (connected) disconnect() else connect()
                    }
                    true
                }
                MotionEvent.ACTION_UP -> {
                    guideLongPressJob?.cancel()
                    guideLongPressJob = null

                    // Restore scale (1.0x) and alpha (1.0f)
                    btnXboxLogo.animate().cancel()
                    btnXboxLogo.animate()
                        .scaleX(1f)
                        .scaleY(1f)
                        .alpha(1f)
                        .setDuration(120L)
                        .start()

                    if (!longPressHandled) {
                        // Short Press (Tap): test haptic vibration + log
                        Log.i(TAG, "Xbox Guide Button Pressed")
                        hapticManager?.playRumble(255, 255)
                    }
                    true
                }
                MotionEvent.ACTION_CANCEL -> {
                    guideLongPressJob?.cancel()
                    guideLongPressJob = null
                    btnXboxLogo.animate().cancel()
                    btnXboxLogo.animate()
                        .scaleX(1f)
                        .scaleY(1f)
                        .alpha(1f)
                        .setDuration(120L)
                        .start()
                    true
                }
                else -> false
            }
        }
    }

    // ── USB Debugging Check & Connection ────────────────────────────────

    private fun isUsbDebuggingEnabled(): Boolean {
        return try {
            android.provider.Settings.Global.getInt(
                contentResolver,
                android.provider.Settings.Global.ADB_ENABLED
            ) == 1
        } catch (e: Exception) {
            false
        }
    }

    private fun setupConnection() {
        btnConnect.setOnClickListener {
            hapticManager?.playTap(200, 30L, btnConnect)
            if (connected) disconnect() else connect()
        }
    }

    private fun connect(bypassUsbDebuggingCheck: Boolean = false) {
        if (!bypassUsbDebuggingCheck && !isUsbDebuggingEnabled()) {
            waitingForUsbDebuggingSettings = true
            AlertDialog.Builder(this)
                .setTitle("USB Debugging Required")
                .setMessage(
                    "To use the USB cable connection seamlessly, USB Debugging must be enabled. " +
                        "Please turn it on in Developer Options."
                )
                .setPositiveButton("Open Settings") { _, _ ->
                    try {
                        startActivity(Intent(Settings.ACTION_APPLICATION_DEVELOPMENT_SETTINGS))
                    } catch (e: Exception) {
                        Log.w(TAG, "Failed to open Developer Options directly: ${e.message}")
                        try {
                            startActivity(Intent(Settings.ACTION_SETTINGS))
                        } catch (_: Exception) {}
                        Toast.makeText(
                            this,
                            "Please open Developer Options in Settings and enable USB Debugging.",
                            Toast.LENGTH_LONG
                        ).show()
                    }
                }
                .setNegativeButton("Use Wi-Fi Only") { dialog, _ ->
                    waitingForUsbDebuggingSettings = false
                    dialog.dismiss()
                    connect(bypassUsbDebuggingCheck = true)
                }
                .setCancelable(true)
                .show()
            return
        }

        waitingForUsbDebuggingSettings = false

        val ip = editIp.text.toString().trim()
        if (ip.isEmpty()) return

        val manager = NetworkManager()
        manager.start(ip)
        networkManager = manager
        if (hapticManager == null) {
            hapticManager = HapticManager(this)
        }
        manager.hapticSink = { left, right -> hapticManager?.playRumble(left, right) }
        manager.effectSink = { effectId, intensity -> hapticManager?.playEffect(effectId, intensity) }
        connected = true

        btnConnect.text = "Disconnect"
        btnConnect.backgroundTintList = ColorStateList.valueOf(Color.parseColor("#E81123"))

        // Observe transport changes and update the UI status indicator
        transportJob?.cancel()
        transportJob = lifecycleScope.launch {
            manager.transport.collectLatest { transport ->
                updateTransportUI(transport)
            }
        }

        startInputLoop()

        // One-shot haptic self-test so we can confirm the pipeline works
        lifecycleScope.launch {
            delay(1500)
            hapticManager?.playRumble(200, 200)
        }
    }

    private fun disconnect() {
        transportJob?.cancel()
        transportJob = null
        inputLoopJob?.cancel()
        inputLoopJob = null
        networkManager?.hapticSink = null
        networkManager?.effectSink = null
        hapticManager?.cancelAll()
        networkManager?.destroy()
        networkManager = null
        connected = false

        btnConnect.text = "Connect"
        btnConnect.backgroundTintList = ColorStateList.valueOf(Color.parseColor("#107C10"))
        updateTransportUI(NetworkManager.Transport.DISCONNECTED)
    }

    /**
     * Update the top-center USB port connection indicator with a smooth ~300ms animation:
     * - USB_TCP      -> ic_usb,  "USB CONNECTED",   Green  (#107C10)
     * - WIFI_UDP     -> ic_wifi, "WI-FI CONNECTED", Yellow (#FFB900)
     * - DISCONNECTED -> ic_wifi, "DISCONNECTED",    Red    (#E81123)
     */
    private fun updateTransportUI(transport: NetworkManager.Transport) {
        val (iconRes, label, hexColor) = when (transport) {
            NetworkManager.Transport.USB_TCP ->
                Triple(R.drawable.ic_usb, "USB CONNECTED", "#107C10")
            NetworkManager.Transport.WIFI_UDP ->
                Triple(R.drawable.ic_wifi, "WI-FI CONNECTED", "#FFB900")
            NetworkManager.Transport.DISCONNECTED ->
                Triple(R.drawable.ic_wifi, "DISCONNECTED", "#E81123")
        }

        val colorInt = Color.parseColor(hexColor)

        // Keep legacy status views synced if present
        tvStatus?.text = label
        tvStatus?.setTextColor(colorInt)
        statusDot?.backgroundTintList = ColorStateList.valueOf(colorInt)

        // If this is the very first state or identical to the currently displayed state, apply without re-animating
        if (displayedTransport == transport) {
            return
        }
        val isInitialRender = (displayedTransport == null)
        displayedTransport = transport

        animateConnectionIndicator(
            iconRes = iconRes,
            label = label,
            colorInt = colorInt,
            animate = !isInitialRender
        )
    }

    /**
     * Smooth ~300ms (150ms out + 150ms in) ViewPropertyAnimator transition for the
     * top-center USB/Wi-Fi icon and label. Cancels any running animations first so
     * rapid transport switches never overlap or crash.
     */
    private fun animateConnectionIndicator(
        @DrawableRes iconRes: Int,
        label: String,
        colorInt: Int,
        animate: Boolean = true
    ) {
        imgConnectionStatus.animate().cancel()
        tvConnectionText.animate().cancel()

        if (!animate || isFinishing || isDestroyed) {
            imgConnectionStatus.setImageResource(iconRes)
            ImageViewCompat.setImageTintList(imgConnectionStatus, ColorStateList.valueOf(colorInt))
            imgConnectionStatus.alpha = 1f
            imgConnectionStatus.scaleX = 1f
            imgConnectionStatus.scaleY = 1f

            tvConnectionText.text = label
            tvConnectionText.setTextColor(colorInt)
            tvConnectionText.alpha = 1f
            return
        }

        // Phase 1 (150ms): Fade out (alpha 1 -> 0) and shrink slightly (scale 1 -> 0.8)
        tvConnectionText.animate()
            .alpha(0f)
            .setDuration(150L)
            .setInterpolator(AccelerateInterpolator())
            .start()

        imgConnectionStatus.animate()
            .alpha(0f)
            .scaleX(0.8f)
            .scaleY(0.8f)
            .setDuration(150L)
            .setInterpolator(AccelerateInterpolator())
            .withEndAction {
                if (isFinishing || isDestroyed) return@withEndAction

                // Swap drawable, tint, and text at the midpoint
                imgConnectionStatus.setImageResource(iconRes)
                ImageViewCompat.setImageTintList(
                    imgConnectionStatus,
                    ColorStateList.valueOf(colorInt)
                )
                tvConnectionText.text = label
                tvConnectionText.setTextColor(colorInt)

                // Phase 2 (150ms): Fade in (alpha 0 -> 1) and scale back up (0.8 -> 1.0)
                tvConnectionText.animate()
                    .alpha(1f)
                    .setDuration(150L)
                    .setInterpolator(DecelerateInterpolator())
                    .start()

                imgConnectionStatus.animate()
                    .alpha(1f)
                    .scaleX(1f)
                    .scaleY(1f)
                    .setDuration(150L)
                    .setInterpolator(DecelerateInterpolator())
                    .start()
            }
            .start()
    }

    // ── 60 Hz input loop ────────────────────────────────────────────────

    private fun startInputLoop() {
        inputLoopJob = lifecycleScope.launch {
            while (isActive && connected) {
                networkManager?.sendInput(
                    buttons = currentButtons,
                    lx = currentLX,
                    ly = currentLY,
                    rx = currentRX,
                    ry = currentRY,
                    lt = currentLT,
                    rt = currentRT
                )
                delay(16)   // ≈ 60 Hz
            }
        }
    }

    // ── Joysticks ───────────────────────────────────────────────────────

    private fun setupJoysticks() {
        joystickLeft.onStickMove = { x, y ->
            currentLX = x
            currentLY = y
        }
        joystickLeft.onHapticEvent = { intensity, durationMs ->
            hapticManager?.playTap(intensity, durationMs, joystickLeft)
        }

        joystickRight.onStickMove = { x, y ->
            currentRX = x
            currentRY = y
        }
        joystickRight.onHapticEvent = { intensity, durationMs ->
            hapticManager?.playTap(intensity, durationMs, joystickRight)
        }
    }

    // ── Face buttons (A B X Y) ──────────────────────────────────────────

    private fun setupFaceButtons() {
        bindButton(btnA, GamepadProtocol.BTN_A)
        bindButton(btnB, GamepadProtocol.BTN_B)
        bindButton(btnX, GamepadProtocol.BTN_X)
        bindButton(btnY, GamepadProtocol.BTN_Y)
    }

    // ── D-Pad ───────────────────────────────────────────────────────────

    private fun setupDPad() {
        bindButton(btnDpadUp,    GamepadProtocol.BTN_DPAD_UP)
        bindButton(btnDpadDown,  GamepadProtocol.BTN_DPAD_DOWN)
        bindButton(btnDpadLeft,  GamepadProtocol.BTN_DPAD_LEFT)
        bindButton(btnDpadRight, GamepadProtocol.BTN_DPAD_RIGHT)
    }

    // ── Shoulder buttons (LB / RB) ──────────────────────────────────────

    private fun setupShoulders() {
        bindButton(btnLB, GamepadProtocol.BTN_LB)
        bindButton(btnRB, GamepadProtocol.BTN_RB)
    }

    // ── Triggers (LT / RT) ──────────────────────────────────────────────

    private fun setupTriggers() {
        triggerLT.onTriggerChange = { value ->
            currentLT = value
        }
        triggerLT.onHapticEvent = { intensity, durationMs ->
            hapticManager?.playTap(intensity, durationMs, triggerLT)
        }

        triggerRT.onTriggerChange = { value ->
            currentRT = value
        }
        triggerRT.onHapticEvent = { intensity, durationMs ->
            hapticManager?.playTap(intensity, durationMs, triggerRT)
        }
    }

    // ── Center buttons (Back / Start) ───────────────────────────────────

    private fun setupCenterButtons() {
        bindButton(btnBack,  GamepadProtocol.BTN_BACK)
        bindButton(btnStart, GamepadProtocol.BTN_START)
    }

    // ── Profile switcher ────────────────────────────────────────────────

    private fun setupProfileToggle() {
        updateProfileUI()
        btnProfile.setOnClickListener {
            hapticManager?.playTap(160, 25L, btnProfile)
            profileIsTypeA = !profileIsTypeA
            updateProfileUI()
        }
    }

    private fun updateProfileUI() {
        if (profileIsTypeA) {
            btnProfile.text = "⚙ Type A"
            tvComboInfo.text = "Grab: LB+B  •  Vanish: LB+A"
        } else {
            btnProfile.text = "⚙ Type C"
            tvComboInfo.text = "Grab: RB+B  •  Vanish: RB+LB"
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    /**
     * Attach a touch listener to [view] that sets / clears [bit] in the
     * button mask on press / release, triggers immediate tactile vibration,
     * and sends an immediate packet on each event so latency stays under one 60-Hz frame.
     */
    @SuppressLint("ClickableViewAccessibility")
    private fun bindButton(view: View, bit: Int) {
        view.setOnTouchListener { v, event ->
            when (event.actionMasked) {
                MotionEvent.ACTION_DOWN -> {
                    hapticManager?.playTap(190, 28L, v)
                    setButton(bit)
                    sendImmediate()
                    true
                }
                MotionEvent.ACTION_UP, MotionEvent.ACTION_CANCEL -> {
                    clearButton(bit)
                    sendImmediate()
                    true
                }
                else -> false
            }
        }
    }

    @Synchronized
    private fun setButton(bit: Int) {
        currentButtons = currentButtons or (1 shl bit)
    }

    @Synchronized
    private fun clearButton(bit: Int) {
        currentButtons = currentButtons and (1 shl bit).inv()
    }

    /** Fire a packet right now so button presses aren't gated by the 16 ms loop. */
    private fun sendImmediate() {
        networkManager?.sendInput(
            buttons = currentButtons,
            lx = currentLX,
            ly = currentLY,
            rx = currentRX,
            ry = currentRY,
            lt = currentLT,
            rt = currentRT
        )
    }
}
