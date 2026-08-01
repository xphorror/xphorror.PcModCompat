package example.pccompat;

import android.content.Intent;
import android.os.Bundle;
import android.view.KeyEvent;
import android.view.MotionEvent;
import android.view.View;

import com.fizzd.connectedworlds.editorport.StArrayModManagerBootstrap;
import com.unity3d.player.UnityPlayerActivity;

/**
 * PcCompat 所需的最小 UnityPlayerActivity 接入模板。
 *
 * 将包名和类名调整为宿主实际入口，并在 AndroidManifest.xml 中注册。
 */
public class PcCompatUnityPlayerActivity extends UnityPlayerActivity {
    private static final int TOUCH_OWNER_NONE = 0;
    private static final int TOUCH_OWNER_UNITY_MODAL = 1;
    private static final int TOUCH_OWNER_MOD_MANAGER = 2;
    private static final int TOUCH_OWNER_UNITY_GAMEPLAY = 3;

    private int touchOwner = TOUCH_OWNER_NONE;

    static {
        // AsyncInput 必须先加载，Bootstrap 随后加载 starray_modmanager。
        System.loadLibrary("AsyncInput");
    }

    @Override
    protected void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);
        StArrayModManagerBootstrap.startInBackground(this);
    }

    @Override
    public boolean dispatchTouchEvent(MotionEvent event) {
        if (event == null) {
            return false;
        }

        View decor = getWindow().getDecorView();
        int width = decor.getWidth();
        int height = decor.getHeight();
        int action = event.getActionMasked();
        boolean deliveredToOverlay = false;
        boolean deliveredToObserver = false;

        if (action == MotionEvent.ACTION_DOWN || touchOwner == TOUCH_OWNER_NONE) {
            if (StArrayModManagerBootstrap.isModalInputCaptureActive() != 0) {
                touchOwner = TOUCH_OWNER_UNITY_MODAL;
            } else if (StArrayModManagerBootstrap.forwardMotionEvent(event)) {
                deliveredToOverlay = true;
                touchOwner = TOUCH_OWNER_MOD_MANAGER;
            } else {
                StArrayModManagerBootstrap.observeGameplayMotionEvent(event, width, height);
                deliveredToObserver = true;
                touchOwner = TOUCH_OWNER_UNITY_GAMEPLAY;
            }
        }

        switch (touchOwner) {
            case TOUCH_OWNER_UNITY_MODAL:
                return finishTouch(event, super.dispatchTouchEvent(event));

            case TOUCH_OWNER_MOD_MANAGER:
                if (!deliveredToOverlay) {
                    StArrayModManagerBootstrap.forwardMotionEvent(event);
                }
                return finishTouch(event, true);

            case TOUCH_OWNER_UNITY_GAMEPLAY:
            default:
                if (!deliveredToObserver) {
                    StArrayModManagerBootstrap.observeGameplayMotionEvent(
                            event, width, height);
                }
                return finishTouch(event, super.dispatchTouchEvent(event));
        }
    }

    @Override
    public boolean dispatchKeyEvent(KeyEvent event) {
        if (event != null &&
                StArrayModManagerBootstrap.isModalInputCaptureActive() != 0 &&
                event.getKeyCode() == KeyEvent.KEYCODE_BACK) {
            if (event.getAction() == KeyEvent.ACTION_UP) {
                StArrayModManagerBootstrap.requestModalClose();
            }
            return true;
        }

        if (event != null) {
            StArrayModManagerBootstrap.observeGameplayKeyEvent(event);
        }

        return super.dispatchKeyEvent(event);
    }

    @Override
    protected void onActivityResult(int requestCode, int resultCode, Intent data) {
        if (!StArrayModManagerBootstrap.handleActivityResult(
                requestCode, resultCode, data)) {
            super.onActivityResult(requestCode, resultCode, data);
        }
    }

    @Override
    protected void onPause() {
        resetTouchOwner();
        super.onPause();
    }

    @Override
    protected void onResume() {
        super.onResume();
        resetTouchOwner();
    }

    @Override
    protected void onDestroy() {
        resetTouchOwner();
        super.onDestroy();
    }

    private boolean finishTouch(MotionEvent event, boolean result) {
        int action = event.getActionMasked();
        if (action == MotionEvent.ACTION_UP || action == MotionEvent.ACTION_CANCEL) {
            resetTouchOwner();
        }
        return result;
    }

    private void resetTouchOwner() {
        touchOwner = TOUCH_OWNER_NONE;
    }
}
