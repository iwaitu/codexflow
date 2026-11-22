package dev.codexflow.mobile

import android.Manifest
import android.content.Intent
import android.content.pm.PackageManager
import android.graphics.Color
import android.os.Bundle
import android.view.Gravity
import android.view.ViewGroup
import android.widget.Button
import android.widget.FrameLayout
import android.widget.TextView
import androidx.activity.result.contract.ActivityResultContracts
import androidx.appcompat.app.AppCompatActivity
import androidx.camera.core.CameraSelector
import androidx.camera.core.ImageAnalysis
import androidx.camera.core.ImageProxy
import androidx.camera.core.Preview
import androidx.camera.lifecycle.ProcessCameraProvider
import androidx.camera.view.PreviewView
import androidx.core.content.ContextCompat
import com.google.zxing.BarcodeFormat
import com.google.zxing.BinaryBitmap
import com.google.zxing.DecodeHintType
import com.google.zxing.PlanarYUVLuminanceSource
import com.google.zxing.ReaderException
import com.google.zxing.common.HybridBinarizer
import com.google.zxing.qrcode.QRCodeReader
import java.util.concurrent.Executors
import java.util.concurrent.atomic.AtomicBoolean

class QrScannerActivity : AppCompatActivity() {

    companion object {
        const val EXTRA_RAW_URI = "rawUri"
        const val EXTRA_ERROR = "error"
    }

    private lateinit var previewView: PreviewView
    private val analysisExecutor = Executors.newSingleThreadExecutor()
    private val qrReader = QRCodeReader()
    private val completed = AtomicBoolean(false)

    private val cameraPermLauncher =
        registerForActivityResult(ActivityResultContracts.RequestPermission()) { granted ->
            if (granted) {
                startCamera()
            } else {
                finishWithError("未授予相机权限，无法扫描配对二维码。")
            }
        }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)

        val root = FrameLayout(this).apply {
            setBackgroundColor(Color.BLACK)
        }
        previewView = PreviewView(this).apply {
            layoutParams = FrameLayout.LayoutParams(
                ViewGroup.LayoutParams.MATCH_PARENT,
                ViewGroup.LayoutParams.MATCH_PARENT,
            )
        }
        root.addView(previewView)

        val cancelButton = Button(this).apply {
            text = "取消"
            setTextColor(Color.WHITE)
            setBackgroundColor(0x99000000.toInt())
            val lp = FrameLayout.LayoutParams(
                ViewGroup.LayoutParams.WRAP_CONTENT,
                ViewGroup.LayoutParams.WRAP_CONTENT,
                Gravity.TOP or Gravity.START,
            )
            lp.setMargins(36, 72, 36, 36)
            layoutParams = lp
            setOnClickListener {
                finishWithError("用户已取消扫码配对。")
            }
        }
        root.addView(cancelButton)

        val hintText = TextView(this).apply {
            text = "对准桌面端「设置 → 远程控制」配对二维码"
            setTextColor(Color.WHITE)
            setBackgroundColor(0x99000000.toInt())
            setPadding(32, 18, 32, 18)
            val lp = FrameLayout.LayoutParams(
                ViewGroup.LayoutParams.WRAP_CONTENT,
                ViewGroup.LayoutParams.WRAP_CONTENT,
                Gravity.BOTTOM or Gravity.CENTER_HORIZONTAL,
            )
            lp.setMargins(36, 36, 36, 96)
            layoutParams = lp
        }
        root.addView(hintText)

        setContentView(root)

        if (ContextCompat.checkSelfPermission(this, Manifest.permission.CAMERA) ==
            PackageManager.PERMISSION_GRANTED
        ) {
            startCamera()
        } else {
            cameraPermLauncher.launch(Manifest.permission.CAMERA)
        }
    }

    private fun startCamera() {
        val providerFuture = ProcessCameraProvider.getInstance(this)
        providerFuture.addListener({
            try {
                val cameraProvider = providerFuture.get()
                val preview = Preview.Builder().build().also {
                    it.surfaceProvider = previewView.surfaceProvider
                }
                val analysis = ImageAnalysis.Builder()
                    .setBackpressureStrategy(ImageAnalysis.STRATEGY_KEEP_ONLY_LATEST)
                    .build()
                    .also { imgAnalysis ->
                        imgAnalysis.setAnalyzer(analysisExecutor) { proxy ->
                            decodeQrFrame(proxy)
                        }
                    }
                cameraProvider.unbindAll()
                cameraProvider.bindToLifecycle(
                    this,
                    CameraSelector.DEFAULT_BACK_CAMERA,
                    preview,
                    analysis,
                )
            } catch (e: Exception) {
                finishWithError("无法启动相机预览：${e.message}")
            }
        }, ContextCompat.getMainExecutor(this))
    }

    private fun decodeQrFrame(image: ImageProxy) {
        if (completed.get()) {
            image.close()
            return
        }
        try {
            val plane = image.planes.firstOrNull() ?: return
            val buffer = plane.buffer
            val data = ByteArray(buffer.remaining())
            buffer.get(data)

            val source = PlanarYUVLuminanceSource(
                data,
                plane.rowStride,
                image.height,
                0,
                0,
                image.width,
                image.height,
                false,
            )
            val bitmap = BinaryBitmap(HybridBinarizer(source))
            val hints = mapOf(DecodeHintType.POSSIBLE_FORMATS to listOf(BarcodeFormat.QR_CODE))
            val result = qrReader.decode(bitmap, hints)
            val text = result.text
            if (!text.isNullOrEmpty() && completed.compareAndSet(false, true)) {
                runOnUiThread {
                    setResult(RESULT_OK, Intent().putExtra(EXTRA_RAW_URI, text))
                    finish()
                }
            }
        } catch (_: ReaderException) {
            // No QR code detected in this frame
        } catch (_: Exception) {
            // Ignore malformed frame buffer edge cases
        } finally {
            qrReader.reset()
            image.close()
        }
    }

    private fun finishWithError(message: String) {
        if (completed.compareAndSet(false, true)) {
            setResult(RESULT_CANCELED, Intent().putExtra(EXTRA_ERROR, message))
            finish()
        }
    }

    override fun onDestroy() {
        analysisExecutor.shutdown()
        super.onDestroy()
    }
}
