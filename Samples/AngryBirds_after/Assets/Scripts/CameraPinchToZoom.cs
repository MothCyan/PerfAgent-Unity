using UnityEngine;
using System.Collections;


/// <summary>
/// Found in 
/// http://unity3d.com/pt/learn/tutorials/modules/beginner/platform-specific/pinch-zoom
/// Contains both perspective and orthographic stuff, in this 2D game we'll
/// be using only the orthographic one
/// </summary>
public class CameraPinchToZoom : MonoBehaviour
{
    public float perspectiveZoomSpeed = 0.5f;        // The rate of change of the field of view in perspective mode.
    public float orthoZoomSpeed = 0.5f;        // The rate of change of the orthographic size in orthographic mode.

    // 优化：相机引用只取一次（原写法每帧最多调 5 次 GetComponent<Camera>）
    Camera cachedCamera;

    void Awake()
    {
        cachedCamera = GetComponent<Camera>();
    }


    void Update()
    {
        // 优化：没有触摸时直接返回，连输入查询都省了
        if (Input.touchCount < 2) return;

        // If there are two touches on the device...
        if (Input.touchCount == 2)
        {
            // Store both touches.
            Touch touchZero = Input.GetTouch(0);
            Touch touchOne = Input.GetTouch(1);

            // Find the position in the previous frame of each touch.
            Vector2 touchZeroPrevPos = touchZero.position - touchZero.deltaPosition;
            Vector2 touchOnePrevPos = touchOne.position - touchOne.deltaPosition;

            // Find the magnitude of the vector (the distance) between the touches in each frame.
            float prevTouchDeltaMag = (touchZeroPrevPos - touchOnePrevPos).magnitude;
            float touchDeltaMag = (touchZero.position - touchOne.position).magnitude;

            // Find the difference in the distances between each frame.
            float deltaMagnitudeDiff = prevTouchDeltaMag - touchDeltaMag;

            // If the camera is orthographic...
            if (cachedCamera.orthographic)
            {
                // ... change the orthographic size based on the change in distance between the touches.
                float orthographicSize = cachedCamera.orthographicSize + deltaMagnitudeDiff * orthoZoomSpeed;

                // Make sure the orthographic size never drops below zero.
                cachedCamera.orthographicSize = Mathf.Clamp(orthographicSize, 3f, 5f);
            }
            else //perspective
            {
                // Otherwise change the field of view based on the change in distance between the touches.
                float fieldOfView = cachedCamera.fieldOfView + deltaMagnitudeDiff * perspectiveZoomSpeed;

                // Clamp the field of view to make sure it's between 0 and 180.
                cachedCamera.fieldOfView = Mathf.Clamp(fieldOfView, 0.1f, 179.9f);
            }
        }
    }
}