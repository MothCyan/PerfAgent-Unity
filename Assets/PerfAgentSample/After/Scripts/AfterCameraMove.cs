using UnityEngine;
using UnityEngine.Serialization;
using System.Collections;
using Assets.Scripts;

public class AfterCameraMove : MonoBehaviour
{


    // Update is called once per frame
    void Update()
    {
        if (AfterSlingShot.slingshotState == AfterSlingshotState.Idle && AfterGameManager.CurrentGameState == AfterGameState.Playing)
        {
            //drag start
            if (Input.GetMouseButtonDown(0))
            {
                timeDragStarted = Time.time;
                dragSpeed = 0f;
                previousPosition = Input.mousePosition;
            }
            //we calculate time difference in order for the following code
            //NOT to run on single taps ;)
            else if (Input.GetMouseButton(0) && Time.time - timeDragStarted > 0.05f)
            {
                //find the delta of this point with the previous frame
                Vector3 input = Input.mousePosition;
                float deltaX = (previousPosition.x - input.x)  * dragSpeed;
                float deltaY = (previousPosition.y - input.y) * dragSpeed;
                //clamp the values so that we drag within limits
                float newX = Mathf.Clamp(transform.position.x + deltaX, 0, 13.36336f);
                float newY = Mathf.Clamp(transform.position.y + deltaY, 0, 2.715f);
                //move camera
                transform.position = new Vector3(
                    newX,
                    newY,
                    transform.position.z);

                previousPosition = input;
                //some small acceleration ;)
                if(dragSpeed < 0.1f) dragSpeed += 0.002f;
            }
        }
    }

    private float dragSpeed = 0.01f;
    private float timeDragStarted;
    private Vector3 previousPosition = Vector3.zero;

    // 序列化字段名从上游的 SlingShot 改成了 AfterSlingShot。
    // 场景里存的是**字段名**，改名不会跟着改场景 —— 没有这行保险时，字段在运行时就是 null，
    // Update 里解引用直接抛 NullReferenceException（实测踩过）。
    [FormerlySerializedAs("SlingShot")]
    public AfterSlingShot AfterSlingShot;
}
