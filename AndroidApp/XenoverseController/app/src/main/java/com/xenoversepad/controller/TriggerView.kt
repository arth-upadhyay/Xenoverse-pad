package com.xenoversepad.controller

import android.annotation.SuppressLint
import android.content.Context
import android.graphics.Canvas
import android.graphics.Color
import android.graphics.LinearGradient
import android.graphics.Paint
import android.graphics.Path
import android.graphics.RectF
import android.graphics.Shader
import android.graphics.Typeface
import android.util.AttributeSet
import android.view.MotionEvent
import android.view.View

/**
 * Curved Xbox Series X analog trigger that engages immediately on press
 * (so holding LT/RT while pressing Y/X/A/B for Super/Ultimate attacks works
 * without needing to drag your finger downward into the face buttons) while
 * still supporting analog modulation (180..255) across the trigger height.
 */
class TriggerView @JvmOverloads constructor(
    context: Context,
    attrs: AttributeSet? = null,
    defStyleAttr: Int = 0
) : View(context, attrs, defStyleAttr) {

    /** Fires on every value change. 0 = released, 255 = fully pressed. */
    var onTriggerChange: ((value: Int) -> Unit)? = null

    /** Optional callback for tactile haptics on trigger pull / full bottom-out. */
    var onHapticEvent: ((intensity: Int, durationMs: Long) -> Unit)? = null

    /** Accent colour for the fill bar. Set from the Activity or XML. */
    var fillColor: Int = Color.parseColor("#107C10")

    private var triggerValue = 0  // 0–255
    private var maxedOut = false

    private val bgPaint = Paint(Paint.ANTI_ALIAS_FLAG).apply {
        style = Paint.Style.FILL
    }

    private val fillPaint = Paint(Paint.ANTI_ALIAS_FLAG).apply {
        style = Paint.Style.FILL
    }

    private val borderPaint = Paint(Paint.ANTI_ALIAS_FLAG).apply {
        color = Color.parseColor("#4A4A54")
        style = Paint.Style.STROKE
        strokeWidth = 2.5f
    }

    private val ridgePaint = Paint(Paint.ANTI_ALIAS_FLAG).apply {
        color = Color.parseColor("#33FFFFFF")
        style = Paint.Style.STROKE
        strokeWidth = 1.5f
    }

    private val labelPaint = Paint(Paint.ANTI_ALIAS_FLAG).apply {
        color = Color.parseColor("#F2F2F7")
        textAlign = Paint.Align.CENTER
        typeface = Typeface.DEFAULT_BOLD
        setShadowLayer(3f, 0f, 1f, Color.BLACK)
    }

    private val rect = RectF()
    private val clipPath = Path()

    override fun onSizeChanged(w: Int, h: Int, oldw: Int, oldh: Int) {
        super.onSizeChanged(w, h, oldw, oldh)
        labelPaint.textSize = (minOf(w, h) * 0.34f).coerceAtLeast(22f)
        if (h > 0) {
            bgPaint.shader = LinearGradient(
                0f, 0f, 0f, h.toFloat(),
                Color.parseColor("#2A2A30"),
                Color.parseColor("#121215"),
                Shader.TileMode.CLAMP
            )
        }
    }

    override fun onDraw(canvas: Canvas) {
        val density = resources.displayMetrics.density
        val w = width.toFloat()
        val h = height.toFloat()
        val pad = 2f * density

        rect.set(pad, pad, w - pad, h - pad)
        val radii = floatArrayOf(
            16f * density, 16f * density, // top-left curved shoulder contour
            16f * density, 16f * density, // top-right curved shoulder contour
            8f * density, 8f * density,   // bottom-right
            8f * density, 8f * density    // bottom-left
        )

        clipPath.reset()
        clipPath.addRoundRect(rect, radii, Path.Direction.CW)

        // 1. Sculpted dark gunmetal trigger housing
        canvas.save()
        canvas.clipPath(clipPath)
        canvas.drawRect(rect, bgPaint)

        // 2. Smooth analog fill rising from bottom (0..255)
        if (triggerValue > 0) {
            val fillH = (h - 2 * pad) * (triggerValue / 255f)
            fillPaint.color = fillColor
            canvas.drawRect(pad, h - pad - fillH, w - pad, h - pad, fillPaint)
        }

        // 3. Subtle Xbox Series X tactile grip ridges
        val step = h / 5f
        for (i in 1..3) {
            val ry = i * step
            canvas.drawLine(w * 0.25f, ry, w * 0.75f, ry, ridgePaint)
        }
        canvas.restore()

        // 4. Metallic outer bevel border
        canvas.drawPath(clipPath, borderPaint)

        // 5. Crisp 0–255 analog readout
        val label = if (triggerValue == 0) "0" else triggerValue.toString()
        val textY = h / 2f - (labelPaint.descent() + labelPaint.ascent()) / 2f
        canvas.drawText(label, w / 2f, textY, labelPaint)
    }

    @SuppressLint("ClickableViewAccessibility")
    override fun onTouchEvent(event: MotionEvent): Boolean {
        when (event.actionMasked) {
            MotionEvent.ACTION_DOWN -> {
                maxedOut = false
                onHapticEvent?.invoke(160, 22L)
                updateTriggerFromEvent(event)
            }
            MotionEvent.ACTION_MOVE -> {
                updateTriggerFromEvent(event)
            }
            MotionEvent.ACTION_UP, MotionEvent.ACTION_CANCEL -> {
                triggerValue = 0
                maxedOut = false
                onTriggerChange?.invoke(0)
                invalidate()
            }
        }
        return true
    }

    private fun updateTriggerFromEvent(event: MotionEvent) {
        val ratio = (event.y / height.coerceAtLeast(1)).coerceIn(0f, 1f)
        // Map any touch immediately into the active trigger zone (185..255) so holding LT/RT
        // at the top corner immediately activates Xenoverse Super/Ultimate menus without dragging down.
        triggerValue = (185 + (ratio * 70f).toInt()).coerceIn(185, 255)
        if (triggerValue >= 245 && !maxedOut) {
            maxedOut = true
            onHapticEvent?.invoke(200, 25L)
        } else if (triggerValue < 225) {
            maxedOut = false
        }
        onTriggerChange?.invoke(triggerValue)
        invalidate()
    }
}
