package com.xenoversepad.controller

import android.annotation.SuppressLint
import android.content.Context
import android.graphics.Canvas
import android.graphics.Color
import android.graphics.DashPathEffect
import android.graphics.Paint
import android.graphics.RadialGradient
import android.graphics.Shader
import android.util.AttributeSet
import android.view.MotionEvent
import android.view.View
import kotlin.math.hypot
import kotlin.math.min

/**
 * Xbox Series X contoured thumbstick with a recessed dark outer well,
 * inner shadow ring, dynamic shaft shadow, knurled grip ring, and concave cap.
 */
class JoystickView @JvmOverloads constructor(
    context: Context,
    attrs: AttributeSet? = null,
    defStyleAttr: Int = 0
) : View(context, attrs, defStyleAttr) {

    /** Called when the stick moves. Values are in −32768 … 32767 (XInput: +Y is UP/forward). */
    var onStickMove: ((x: Short, y: Short) -> Unit)? = null

    /** Optional callback for tactile haptics on touch down, rim hit, and release. */
    var onHapticEvent: ((intensity: Int, durationMs: Long) -> Unit)? = null

    // Current normalized screen offset (−1.0 … 1.0; screen Y is negative when pushed up)
    private var stickX = 0f
    private var stickY = 0f
    private var wasAtRim = false

    private val wellBezelPaint = Paint(Paint.ANTI_ALIAS_FLAG).apply {
        color = Color.parseColor("#2A2A30")
        style = Paint.Style.STROKE
        strokeWidth = 3f
    }

    private val wellFillPaint = Paint(Paint.ANTI_ALIAS_FLAG).apply {
        style = Paint.Style.FILL
    }

    private val wellInnerShadowPaint = Paint(Paint.ANTI_ALIAS_FLAG).apply {
        color = Color.parseColor("#08080A")
        style = Paint.Style.STROKE
        strokeWidth = 6f
    }

    private val shaftShadowPaint = Paint(Paint.ANTI_ALIAS_FLAG).apply {
        color = Color.parseColor("#AA050507")
        style = Paint.Style.FILL
    }

    private val knobBasePaint = Paint(Paint.ANTI_ALIAS_FLAG).apply {
        color = Color.parseColor("#25252B")
        style = Paint.Style.FILL
    }

    private val knobRimPaint = Paint(Paint.ANTI_ALIAS_FLAG).apply {
        color = Color.parseColor("#3E3E47")
        style = Paint.Style.STROKE
        strokeWidth = 2.5f
    }

    private val knurlPaint = Paint(Paint.ANTI_ALIAS_FLAG).apply {
        color = Color.parseColor("#52525E")
        style = Paint.Style.STROKE
        strokeWidth = 4f
        pathEffect = DashPathEffect(floatArrayOf(5f, 4f), 0f)
    }

    private val concaveDishPaint = Paint(Paint.ANTI_ALIAS_FLAG).apply {
        style = Paint.Style.FILL
    }

    private val dishRimPaint = Paint(Paint.ANTI_ALIAS_FLAG).apply {
        color = Color.parseColor("#141418")
        style = Paint.Style.STROKE
        strokeWidth = 2f
    }

    override fun onSizeChanged(w: Int, h: Int, oldw: Int, oldh: Int) {
        super.onSizeChanged(w, h, oldw, oldh)
        val cx = w / 2f
        val cy = h / 2f
        val baseRadius = min(cx, cy) * 0.88f
        if (baseRadius > 0f) {
            wellFillPaint.shader = RadialGradient(
                cx, cy, baseRadius,
                intArrayOf(
                    Color.parseColor("#0B0B0D"),
                    Color.parseColor("#151519"),
                    Color.parseColor("#09090B")
                ),
                floatArrayOf(0f, 0.75f, 1f),
                Shader.TileMode.CLAMP
            )
        }
    }

    override fun onDraw(canvas: Canvas) {
        super.onDraw(canvas)
        val cx = width / 2f
        val cy = height / 2f
        val baseRadius = min(cx, cy) * 0.88f
        val maxTravel = baseRadius * 0.52f
        val knobRadius = baseRadius * 0.46f

        // 1. Recessed outer thumbstick well + inner shadow ring
        canvas.drawCircle(cx, cy, baseRadius, wellFillPaint)
        canvas.drawCircle(cx, cy, baseRadius - 3f, wellInnerShadowPaint)
        canvas.drawCircle(cx, cy, baseRadius, wellBezelPaint)

        // 2. Thumbstick center position
        val knobX = cx + stickX * maxTravel
        val knobY = cy + stickY * maxTravel

        // 3. Dynamic drop shadow beneath thumbstick cap
        canvas.drawCircle(
            knobX + stickX * 5f,
            knobY + 6f + stickY * 5f,
            knobRadius * 1.04f,
            shaftShadowPaint
        )

        // 4. Outer thumbstick cap body + textured knurled grip ring
        canvas.drawCircle(knobX, knobY, knobRadius, knobBasePaint)
        canvas.drawCircle(knobX, knobY, knobRadius, knobRimPaint)
        canvas.drawCircle(knobX, knobY, knobRadius * 0.86f, knurlPaint)

        // 5. Inner concave thumb dish
        val dishRadius = knobRadius * 0.68f
        if (dishRadius > 0f) {
            concaveDishPaint.shader = RadialGradient(
                knobX - dishRadius * 0.25f,
                knobY - dishRadius * 0.25f,
                dishRadius * 1.3f,
                intArrayOf(
                    Color.parseColor("#18181C"),
                    Color.parseColor("#222228"),
                    Color.parseColor("#2D2D35")
                ),
                floatArrayOf(0f, 0.7f, 1f),
                Shader.TileMode.CLAMP
            )
        }
        canvas.drawCircle(knobX, knobY, dishRadius, concaveDishPaint)
        canvas.drawCircle(knobX, knobY, dishRadius, dishRimPaint)
    }

    @SuppressLint("ClickableViewAccessibility")
    override fun onTouchEvent(event: MotionEvent): Boolean {
        when (event.actionMasked) {
            MotionEvent.ACTION_DOWN -> {
                wasAtRim = false
                onHapticEvent?.invoke(140, 20L)
                updateStickFromEvent(event)
            }
            MotionEvent.ACTION_MOVE -> {
                updateStickFromEvent(event)
            }
            MotionEvent.ACTION_UP, MotionEvent.ACTION_CANCEL -> {
                stickX = 0f
                stickY = 0f
                wasAtRim = false
                onHapticEvent?.invoke(90, 15L)
                dispatchValues()
                invalidate()
            }
        }
        return true
    }

    private fun updateStickFromEvent(event: MotionEvent) {
        val cx = width / 2f
        val cy = height / 2f
        val radius = min(cx, cy) * 0.88f

        var dx = (event.x - cx) / radius
        var dy = (event.y - cy) / radius

        // Clamp to unit circle
        val dist = hypot(dx, dy)
        if (dist >= 1f) {
            dx /= dist
            dy /= dist
            if (!wasAtRim) {
                wasAtRim = true
                onHapticEvent?.invoke(170, 22L)
            }
        } else if (dist < 0.88f) {
            wasAtRim = false
        }

        stickX = dx
        stickY = dy

        dispatchValues()
        invalidate()
    }

    private fun dispatchValues() {
        // In Android View coordinates, pushing UP gives negative stickY (-1.0),
        // whereas XInput / Xbox360Controller expects +32767 for UP (forward) and -32768 for DOWN (backward).
        // Negating stickY here fixes the inverted vertical axis while keeping onDraw knob movement natural.
        val ix = (stickX * 32767f).toInt().coerceIn(-32768, 32767).toShort()
        val iy = (-stickY * 32767f).toInt().coerceIn(-32768, 32767).toShort()
        onStickMove?.invoke(ix, iy)
    }
}
