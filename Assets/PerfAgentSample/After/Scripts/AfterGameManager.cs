using UnityEngine;
using System.Collections.Generic;
using Assets.Scripts;
using UnityEngine.SceneManagement;   // 优化：用 SceneManager 代替已废弃的 Application.LoadLevel
using DG.Tweening;

public class AfterGameManager : MonoBehaviour
{

    public AfterCameraFollow cameraFollow;
    int currentBirdIndex;
    public AfterSlingShot slingshot;
    [HideInInspector]
    public static AfterGameState CurrentGameState = AfterGameState.Start;
    private List<GameObject> Bricks;
    private List<GameObject> Birds;
    private List<GameObject> Pigs;

    // 优化：预先缓存刚体引用，避免「停止检测」里每帧逐个 GetComponent
    private readonly List<Rigidbody2D> trackedBodies = new List<Rigidbody2D>();
    // 优化：Camera.main 内部是一次带 Tag 的场景查找，只取一次
    private Camera cachedCamera;

    // Use this for initialization
    void Start()
    {
        CurrentGameState = AfterGameState.Start;
        slingshot.enabled = false;
        //find all relevant game objects
        Bricks = new List<GameObject>(GameObject.FindGameObjectsWithTag("Brick"));
        Birds = new List<GameObject>(GameObject.FindGameObjectsWithTag("Bird"));
        Pigs = new List<GameObject>(GameObject.FindGameObjectsWithTag("Pig"));
        //unsubscribe and resubscribe from the event
        //this ensures that we subscribe only once
        slingshot.BirdThrown -= Slingshot_BirdThrown; slingshot.BirdThrown += Slingshot_BirdThrown;

        // 优化：启动时一次性缓存相机与刚体
        cachedCamera = Camera.main;
        RefreshTrackedBodies();
    }

    /// <summary>
    /// 优化：启动时把场景里的刚体引用收进一个列表。
    /// 游戏过程中砖块/猪会被销毁，检测时用 null 判断跳过即可，
    /// 不再需要每帧遍历对象并逐个 GetComponent。
    /// </summary>
    private void RefreshTrackedBodies()
    {
        trackedBodies.Clear();
        AddBodies(Bricks);
        AddBodies(Pigs);
        AddBodies(Birds);
    }

    private void AddBodies(List<GameObject> source)
    {
        for (int i = 0; i < source.Count; i++)
        {
            if (source[i] == null) continue;
            var body = source[i].GetComponent<Rigidbody2D>();
            if (body != null) trackedBodies.Add(body);
        }
    }


    // Update is called once per frame
    void Update()
    {
        switch (CurrentGameState)
        {
            case AfterGameState.Start:
                //if player taps, begin animating the bird 
                //to the slingshot
                if (Input.GetMouseButtonUp(0))
                {
                    AnimateBirdToSlingshot();
                }
                break;
            case AfterGameState.BirdMovingToSlingshot:
                //do nothing
                break;
            case AfterGameState.Playing:
                //if we have thrown a bird
                //and either everything has stopped moving
                //or there has been 5 seconds since we threw the bird
                //animate the camera to the start position
                if (slingshot.slingshotState == AfterSlingshotState.BirdFlying &&
                    (BricksBirdsPigsStoppedMoving() || Time.time - slingshot.TimeSinceThrown > 5f))
                {
                    slingshot.enabled = false;
                    AnimateCameraToStartPosition();
                    CurrentGameState = AfterGameState.BirdMovingToSlingshot;
                }
                break;
            //if we have won or lost, we will restart the level
            //in a normal game, we would show the "Won" screen 
            //and on tap the user would go to the next level
            case AfterGameState.Won:
            case AfterGameState.Lost:
                if (Input.GetMouseButtonUp(0))
                {
                    // 优化：Application.LoadLevel 已废弃，改用 SceneManager
                    SceneManager.LoadScene(SceneManager.GetActiveScene().buildIndex);
                }
                break;
            default:
                break;
        }
    }


    /// <summary>
    /// A check whether all Pigs are null
    /// i.e. they have been destroyed
    /// </summary>
    /// <returns></returns>
    private bool AllPigsDestroyed()
    {
        // 优化：手写循环，避免 LINQ 的迭代器与闭包分配
        for (int i = 0; i < Pigs.Count; i++)
        {
            if (Pigs[i] != null) return false;
        }

        return true;
    }

    /// <summary>
    /// Animates the camera to the original location
    /// When it finishes, it checks if we have lost, won or we have other birds
    /// available to throw
    /// </summary>
    private void AnimateCameraToStartPosition()
    {
        float duration = Vector2.Distance(cachedCamera.transform.position, cameraFollow.StartingPosition) / 10f;
        if (duration == 0.0f) duration = 0.1f;
        //animate the camera to start
        cachedCamera.transform.DOMove(cameraFollow.StartingPosition, duration). //end position
            OnComplete(() =>
                        {
                            cameraFollow.IsFollowing = false;
                            if (AllPigsDestroyed())
                            {
                                CurrentGameState = AfterGameState.Won;
                            }
                            //animate the next bird, if available
                            else if (currentBirdIndex == Birds.Count - 1)
                            {
                                //no more birds, go to finished
                                CurrentGameState = AfterGameState.Lost;
                            }
                            else
                            {
                                slingshot.slingshotState = AfterSlingshotState.Idle;
                                //bird to throw is the next on the list
                                currentBirdIndex++;
                                AnimateBirdToSlingshot();
                            }
                        });
    }

    /// <summary>
    /// Animates the bird from the waiting position to the slingshot
    /// </summary>
    void AnimateBirdToSlingshot()
    {
        CurrentGameState = AfterGameState.BirdMovingToSlingshot;
        Birds[currentBirdIndex].transform.DOMove
            (slingshot.BirdWaitPosition.transform.position, //final position
            Vector2.Distance(Birds[currentBirdIndex].transform.position / 10,
            slingshot.BirdWaitPosition.transform.position) / 10). //position
                OnComplete(() =>
                        {   CurrentGameState = AfterGameState.Playing;
                            slingshot.enabled = true; //enable slingshot
                            //current bird is the current in the list
                            slingshot.BirdToThrow = Birds[currentBirdIndex];
                        });
    }

    /// <summary>
    /// Event handler, when the bird is thrown, camera starts following it
    /// </summary>
    /// <param name="sender"></param>
    /// <param name="e"></param>
    private void Slingshot_BirdThrown(object sender, System.EventArgs e)
    {
        cameraFollow.BirdToFollow = Birds[currentBirdIndex].transform;
        cameraFollow.IsFollowing = true;
    }

    /// <summary>
    /// Check if all birds, pigs and bricks have stopped moving
    /// </summary>
    /// <returns></returns>
    bool BricksBirdsPigsStoppedMoving()
    {
        // 优化：遍历已缓存的刚体（手写 for，无 LINQ、无 GetComponent、无分配）
        for (int i = 0; i < trackedBodies.Count; i++)
        {
            var body = trackedBodies[i];
            if (body == null) continue;   // 已经被销毁的对象
            if (body.velocity.sqrMagnitude > AfterConstants.MinVelocity)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Found here
    /// http://www.bensilvis.com/?p=500
    /// </summary>
    /// <param name="screenWidth"></param>
    /// <param name="screenHeight"></param>
    public static void AutoResize(int screenWidth, int screenHeight)
    {
        Vector2 resizeRatio = new Vector2((float)Screen.width / screenWidth, (float)Screen.height / screenHeight);
        GUI.matrix = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, new Vector3(resizeRatio.x, resizeRatio.y, 1.0f));
    }

    /// <summary>
    /// Shows relevant GUI depending on the current game state
    /// </summary>
    void OnGUI()
    {
        AutoResize(800, 480);
        switch (CurrentGameState)
        {
            case AfterGameState.Start:
                GUI.Label(new Rect(0, 150, 200, 100), "Tap the screen to start");
                break;
            case AfterGameState.Won:
                GUI.Label(new Rect(0, 150, 200, 100), "You won! Tap the screen to restart");
                break;
            case AfterGameState.Lost:
                GUI.Label(new Rect(0, 150, 200, 100), "You lost! Tap the screen to restart");
                break;
            default:
                break;
        }
    }


}
