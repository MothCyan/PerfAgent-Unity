using UnityEngine;
using System.Collections;
using Assets.Scripts;
using System;
using DG.Tweening;

public class AfterSlingShot : MonoBehaviour
{

    //a vector that points in the middle between left and right parts of the slingshot
    private Vector3 SlingshotMiddleVector;

    [HideInInspector]
    public AfterSlingshotState slingshotState;

    //the left and right parts of the slingshot
    public Transform LeftSlingshotOrigin, RightSlingshotOrigin;

    //two line renderers to simulate the "strings" of the slingshot
    public LineRenderer SlingshotLineRenderer1;
    public LineRenderer SlingshotLineRenderer2;
    
    //this linerenderer will draw the projected trajectory of the thrown bird
    public LineRenderer TrajectoryLineRenderer;
    
    [HideInInspector]
    //the bird to throw
    public GameObject BirdToThrow;

    //the position of the bird tied to the slingshot
    public Transform BirdWaitPosition;

    public float ThrowSpeed;

    // 优化：相机只查一次；轨迹点数组只分配一次（原写法拖拽期间每帧 new Vector2[15]）
    private const int TrajectorySegmentCount = 15;
    private Camera cachedCamera;

    // 优化：原来 Update 里每帧 GetComponent<CircleCollider2D>()。
    // 不能只在 Start 里缓存一次 —— BirdToThrow 每回合都会被 GameManager 换掉
    //（AfterGameManager 里 slingshot.BirdToThrow = Birds[currentBirdIndex]），
    // 所以要比对“上次缓存的那只鸟”，换了（或已被销毁）才重新取。
    private CircleCollider2D cachedBirdCollider;
    private readonly Vector2[] trajectorySegments = new Vector2[TrajectorySegmentCount];

    [HideInInspector]
    public float TimeSinceThrown;

    // Use this for initialization
    void Start()
    {
        cachedCamera = Camera.main;

        //set the sorting layer name for the line renderers
        //for the slingshot renderers this did not work so I
        //set the z on the background sprites to 10
        //hope there's a better way around that!
        SlingshotLineRenderer1.sortingLayerName = "Foreground";
        SlingshotLineRenderer2.sortingLayerName = "Foreground";
        TrajectoryLineRenderer.sortingLayerName = "Foreground";

        slingshotState = AfterSlingshotState.Idle;
        SlingshotLineRenderer1.SetPosition(0, LeftSlingshotOrigin.position);
        SlingshotLineRenderer2.SetPosition(0, RightSlingshotOrigin.position);

        //pointing at the middle position of the two vectors
        SlingshotMiddleVector = new Vector3((LeftSlingshotOrigin.position.x + RightSlingshotOrigin.position.x) / 2,
            (LeftSlingshotOrigin.position.y + RightSlingshotOrigin.position.y) / 2, 0);
    }

    // Update is called once per frame
    void Update()
    {
        switch (slingshotState)
        {
            case AfterSlingshotState.Idle:
                //fix bird's position
                InitializeBird();
                //display the slingshot "strings"
                DisplaySlingshotLineRenderers();
                if (Input.GetMouseButtonDown(0))
                {
                    //get the point on screen user has tapped
                    Vector3 location = cachedCamera.ScreenToWorldPoint(Input.mousePosition);
                    //if user has tapped onto the bird
                    if (BirdCollider() == Physics2D.OverlapPoint(location))
                    {
                        slingshotState = AfterSlingshotState.UserPulling;
                    }
                }
                break;
            case AfterSlingshotState.UserPulling:
                DisplaySlingshotLineRenderers();

                if (Input.GetMouseButton(0))
                {
                    //get where user is tapping
                    Vector3 location = cachedCamera.ScreenToWorldPoint(Input.mousePosition);
                    location.z = 0;
                    //we will let the user pull the bird up to a maximum distance
                    if (Vector3.Distance(location, SlingshotMiddleVector) > 1.5f)
                    {
                        //basic vector maths :)
                        var maxPosition = (location - SlingshotMiddleVector).normalized * 1.5f + SlingshotMiddleVector;
                        BirdToThrow.transform.position = maxPosition;
                    }
                    else
                    {
                        BirdToThrow.transform.position = location;
                    }
                    float distance = Vector3.Distance(SlingshotMiddleVector, BirdToThrow.transform.position);
                    //display projected trajectory based on the distance
                    DisplayTrajectoryLineRenderer2(distance);
                }
                else//user has removed the tap 
                {
                    SetTrajectoryLineRenderesActive(false);
                    //throw the bird!!!
                    TimeSinceThrown = Time.time;
                    float distance = Vector3.Distance(SlingshotMiddleVector, BirdToThrow.transform.position);
                    if (distance > 1)
                    {
                        SetSlingshotLineRenderersActive(false);
                        slingshotState = AfterSlingshotState.BirdFlying;
                        ThrowBird(distance);
                    }
                    else//not pulled long enough, so reinitiate it
                    {
                        //distance/10 was found with trial and error :)
                        //animate the bird to the wait position
                        BirdToThrow.transform.DOMove(BirdWaitPosition.transform.position, //final position
                        distance / 10). //duration
                            OnComplete(() =>
                        {
                            InitializeBird();
                        });

                    }
                }
                break;
            case AfterSlingshotState.BirdFlying:
                break;
            default:
                break;
        }

    }

    private void ThrowBird(float distance)
    {
        //get velocity
        Vector3 velocity = SlingshotMiddleVector - BirdToThrow.transform.position;
        BirdToThrow.GetComponent<AfterBird>().OnThrow(); //make the bird aware of it
        //old and alternative way
        //BirdToThrow.GetComponent<Rigidbody2D>().AddForce
        //    (new Vector2(v2.x, v2.y) * ThrowSpeed * distance * 300 * Time.deltaTime);
        //set the velocity
        BirdToThrow.GetComponent<Rigidbody2D>().velocity = new Vector2(velocity.x, velocity.y) * ThrowSpeed * distance;


        //notify interested parties that the bird was thrown
        if (BirdThrown != null)
            BirdThrown(this, EventArgs.Empty);
    }

    public event EventHandler BirdThrown;

    /// <summary>
    /// 鸟的碰撞体：每回合换鸟后才重新取一次。
    ///
    /// 原来写的是 Update 里每帧 `BirdToThrow.GetComponent<CircleCollider2D>()` ——
    /// 每帧一次 GetComponent 是纯粹的稳态开销（也是脚本反模式扫描会报的那一处）。
    /// 缓存后要注意两件事：
    ///   1. BirdToThrow 每回合会被 GameManager 换掉 —— 所以比对的是“上次缓存的那只鸟”；
    ///   2. 鸟被销毁后 Unity 的 == 会返回 true（伪 null），下面这个判空能自愈。
    /// </summary>
    private CircleCollider2D BirdCollider()
    {
        if (cachedBirdCollider == null || cachedBirdCollider.gameObject != BirdToThrow)
            cachedBirdCollider = BirdToThrow.GetComponent<CircleCollider2D>();
        return cachedBirdCollider;
    }

    private void InitializeBird()
    {
        //initialization of the ready to be thrown bird
        BirdToThrow.transform.position = BirdWaitPosition.position;
        slingshotState = AfterSlingshotState.Idle;
        SetSlingshotLineRenderersActive(true);
    }

    void DisplaySlingshotLineRenderers()
    {
        SlingshotLineRenderer1.SetPosition(1, BirdToThrow.transform.position);
        SlingshotLineRenderer2.SetPosition(1, BirdToThrow.transform.position);
    }

    void SetSlingshotLineRenderersActive(bool active)
    {
        SlingshotLineRenderer1.enabled = active;
        SlingshotLineRenderer2.enabled = active;
    }

    void SetTrajectoryLineRenderesActive(bool active)
    {
        TrajectoryLineRenderer.enabled = active;
    }


    /// <summary>
    /// Another solution (a great one) can be found here
    /// http://wiki.unity3d.com/index.php?title=Trajectory_Simulation
    /// </summary>
    /// <param name="distance"></param>
    void DisplayTrajectoryLineRenderer2(float distance)
    {
        SetTrajectoryLineRenderesActive(true);
        Vector3 v2 = SlingshotMiddleVector - BirdToThrow.transform.position;
        int segmentCount = TrajectorySegmentCount;
        // 优化：复用字段数组，拖拽时不再每帧分配
        Vector2[] segments = trajectorySegments;

        // The first line point is wherever the player's cannon, etc is
        segments[0] = BirdToThrow.transform.position;

        // The initial velocity
        Vector2 segVelocity = new Vector2(v2.x, v2.y) * ThrowSpeed * distance;

        for (int i = 1; i < segmentCount; i++)
        {
            //x axis: spaceX = initialSpaceX + velocityX * time
            //y axis: spaceY = initialSpaceY + velocityY * time + 1/2 * accelerationY * time ^ 2
            //both (vector) space = initialSpace + velocity * time + 1/2 * acceleration * time ^ 2
            float time2 = i * Time.fixedDeltaTime * 5;
            // 优化：t*t 代替 Mathf.Pow(t, 2)
            segments[i] = segments[0] + segVelocity * time2 + 0.5f * Physics2D.gravity * (time2 * time2);
        }

        // 优化：positionCount 代替已废弃的 SetVertexCount
        TrajectoryLineRenderer.positionCount = segmentCount;
        for (int i = 0; i < segmentCount; i++)
            TrajectoryLineRenderer.SetPosition(i, segments[i]);
    }



    ///http://opengameart.org/content/forest-themed-sprites
    ///forest sprites found on opengameart.com
    ///© 2012-2013 Julien Jorge <julien.jorge@stuff-o-matic.com>

}
