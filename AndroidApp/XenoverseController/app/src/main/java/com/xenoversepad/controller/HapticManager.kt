package com.xenoversepad.controller

import android.content.Context
import android.os.SystemClock
import android.os.VibrationAttributes
import android.os.VibrationEffect
import android.os.Vibrator
import android.os.VibratorManager
import android.util.Log
import android.view.HapticFeedbackConstants
import android.view.View

class HapticManager(context: Context) {
    private val tag = "HapticManager"

    private val vibratorManager =
        context.getSystemService(Context.VIBRATOR_MANAGER_SERVICE) as VibratorManager
    private val defaultVibrator: Vibrator = vibratorManager.defaultVibrator
    private val hasAmplitude = defaultVibrator.hasAmplitudeControl()

    // Use USAGE_TOUCH so Android 12+ (API 31+) delivers haptics even if ring/notification vibration is muted
    private val touchAttributes: VibrationAttributes =
        VibrationAttributes.Builder()
            .setUsage(VibrationAttributes.USAGE_TOUCH)
            .build()

    @Volatile private var lastPlayMs = 0L
    @Volatile private var lastEffectEndMs = 0L
    @Volatile private var lastTapMs = 0L

    /**
     * Play incoming PC / game standard rumble ("XVR1", 0..255 per motor).
     * Note: Many PC games (including Xenoverse 1) send a 1-frame (16ms) non-zero rumble
     * followed immediately by (0, 0). We let the 90ms one-shot finish naturally instead
     * of calling cancelAll() within the first 90ms so short game hits are never snuffed out.
     */
    fun playRumble(leftIn: Int, rightIn: Int) {
        val left = leftIn.coerceIn(0, 255)
        val right = rightIn.coerceIn(0, 255)
        val now = SystemClock.elapsedRealtime()

        if (left == 0 && right == 0) {
            // Only cancel if neither a recent rumble pulse nor an XVE1 effect is mid-flight
            if (now - lastPlayMs >= 95L && now >= lastEffectEndMs) {
                cancelAll()
            }
            return
        }

        if (now - lastPlayMs < 20L) return
        lastPlayMs = now

        // Boost low game motor values so subtle XInput rumbles are still clearly felt on phone motors
        val rawAmp = maxOf(left, right)
        val amp = if (rawAmp in 1..79) (rawAmp + 60).coerceAtMost(255) else rawAmp

        try {
            val effect = if (hasAmplitude) {
                VibrationEffect.createOneShot(90L, amp.coerceAtLeast(1))
            } else {
                VibrationEffect.createOneShot(90L, VibrationEffect.DEFAULT_AMPLITUDE)
            }
            defaultVibrator.vibrate(effect, touchAttributes)
            Log.i(tag, "vibrate left=$left right=$right amp=$amp")
        } catch (e: Exception) {
            Log.w(tag, "vibrate failed: ${e.message}")
        }
    }

    /**
     * Phase 7 — Haptic Vocabulary ("XVE1" protocol).
     * Plays custom waveforms scaled by [intensity] (0..255):
     *   1 = Kamehameha Charge (Slow 400ms ramp-up)
     *   2 = Kamehameha Release (Massive 400ms punch)
     *   3 = Heavy Hit (Sharp dual-jolt waveform)
     *   4 = Transform / Awakening (1000ms rising crescendo)
     */
    fun playEffect(effectId: Int, intensity: Int) {
        val clampedIntensity = intensity.coerceIn(0, 255)
        if (clampedIntensity == 0) {
            cancelAll()
            return
        }

        fun scaleAmp(base: Int): Int {
            if (base <= 0) return 0
            return ((base * clampedIntensity) / 255).coerceIn(1, 255)
        }

        fun scaleAmps(bases: IntArray): IntArray =
            IntArray(bases.size) { i -> scaleAmp(bases[i]) }

        val now = SystemClock.elapsedRealtime()

        try {
            val (effect, durationMs) = when (effectId) {
                1 -> {
                    // 1 (Kamehameha Charge): Slow ramp-up (400ms)
                    val timings = longArrayOf(100L, 100L, 100L, 100L)
                    val amps = scaleAmps(intArrayOf(50, 100, 150, 200))
                    val wf = if (hasAmplitude) {
                        VibrationEffect.createWaveform(timings, amps, -1)
                    } else {
                        VibrationEffect.createWaveform(timings, -1)
                    }
                    wf to 400L
                }
                2 -> {
                    // 2 (Kamehameha Release): Massive punch (400ms)
                    val amp = scaleAmp(255)
                    val wf = if (hasAmplitude) {
                        VibrationEffect.createOneShot(400L, amp)
                    } else {
                        VibrationEffect.createOneShot(400L, VibrationEffect.DEFAULT_AMPLITUDE)
                    }
                    wf to 400L
                }
                3 -> {
                    // 3 (Heavy Hit): Sharp jolt (160ms)
                    val timings = longArrayOf(0L, 40L, 40L, 80L)
                    val amps = scaleAmps(intArrayOf(0, 255, 0, 150))
                    val wf = if (hasAmplitude) {
                        VibrationEffect.createWaveform(timings, amps, -1)
                    } else {
                        VibrationEffect.createWaveform(timings, -1)
                    }
                    wf to 160L
                }
                4 -> {
                    // 4 (Transform/Awakening): Rising crescendo over 1 second (1000ms)
                    val timings = LongArray(10) { 100L }
                    val amps = scaleAmps(intArrayOf(25, 50, 75, 105, 135, 165, 195, 220, 240, 255))
                    val wf = if (hasAmplitude) {
                        VibrationEffect.createWaveform(timings, amps, -1)
                    } else {
                        VibrationEffect.createWaveform(timings, -1)
                    }
                    wf to 1000L
                }
                else -> {
                    Log.w(tag, "Unknown effectId=$effectId")
                    return
                }
            }

            lastEffectEndMs = now + durationMs
            defaultVibrator.cancel()
            defaultVibrator.vibrate(effect, touchAttributes)
            Log.i(tag, "playEffect id=$effectId intensity=$clampedIntensity duration=${durationMs}ms")
        } catch (e: Exception) {
            Log.w(tag, "playEffect failed: ${e.message}")
        }
    }

    /**
     * Play local tactile feedback when pressing controller buttons, D-pad, triggers, or joysticks.
     */
    fun playTap(intensity: Int = 180, durationMs: Long = 28L, view: View? = null) {
        view?.performHapticFeedback(
            HapticFeedbackConstants.VIRTUAL_KEY,
            HapticFeedbackConstants.FLAG_IGNORE_VIEW_SETTING
        )

        val now = SystemClock.elapsedRealtime()
        // Do not interrupt a playing XVE1 combat waveform with a light button tap
        if (now < lastEffectEndMs) return
        if (now - lastTapMs < 15L) return
        lastTapMs = now

        val amp = intensity.coerceIn(1, 255)
        try {
            val effect = if (hasAmplitude) {
                VibrationEffect.createOneShot(durationMs, amp)
            } else {
                VibrationEffect.createOneShot(durationMs, VibrationEffect.DEFAULT_AMPLITUDE)
            }
            defaultVibrator.vibrate(effect, touchAttributes)
        } catch (e: Exception) {
            Log.w(tag, "tap vibrate failed: ${e.message}")
        }
    }

    fun cancelAll() {
        try { defaultVibrator.cancel() } catch (_: Exception) {}
    }

    fun release() { cancelAll() }
}
