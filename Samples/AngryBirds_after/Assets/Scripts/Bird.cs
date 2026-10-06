using UnityEngine;
using System.Collections;
using Assets.Scripts;

[RequireComponent(typeof(Rigidbody2D))]
public class Bird : MonoBehaviour
{
    // 优化：组件与 YieldInstruction 都只取一次，FixedUpdate 里不再 GetComponent
    Rigidbody2D body;
    CircleCollider2D circleCollider;
    TrailRenderer trailRenderer;
    AudioSource audioSource;
    static readonly WaitForSeconds DestroyDelay = new WaitForSeconds(2f);
    bool destroyScheduled;

    void Awake()
    {
        body = GetComponent<Rigidbody2D>();
        circleCollider = GetComponent<CircleCollider2D>();
        trailRenderer = GetComponent<TrailRenderer>();
        audioSource = GetComponent<AudioSource>();
    }

    // Use this for initialization
    void Start()
    {
        //trailrenderer is not visible until we throw the bird
        trailRenderer.enabled = false;
        trailRenderer.sortingLayerName = "Foreground";
        //no gravity at first
        body.isKinematic = true;
        //make the collider bigger to allow for easy touching
        circleCollider.radius = Constants.BirdColliderRadiusBig;
        State = BirdState.BeforeThrown;
    }



    void FixedUpdate()
    {
        //if we've thrown the bird
        //and its speed is very small
        //优化：只启动一次协程（原写法在速度恢复时可能重复启动多个销毁协程）
        if (!destroyScheduled && State == BirdState.Thrown &&
            body.velocity.sqrMagnitude <= Constants.MinVelocity)
        {
            destroyScheduled = true;
            //destroy the bird after 2 seconds
            StartCoroutine(DestroyAfter());
        }
    }

    public void OnThrow()
    {
        //play the sound
        audioSource.Play();
        //show the trail renderer
        trailRenderer.enabled = true;
        //allow for gravity forces
        body.isKinematic = false;
        //make the collider normal size
        circleCollider.radius = Constants.BirdColliderRadiusNormal;
        State = BirdState.Thrown;
    }

    IEnumerator DestroyAfter()
    {
        yield return DestroyDelay;
        Destroy(gameObject);
    }

    public BirdState State
    {
        get;
        private set;
    }
}
