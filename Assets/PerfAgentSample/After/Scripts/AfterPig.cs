using UnityEngine;
using System.Collections;

public class AfterPig : MonoBehaviour
{

    public float Health = 150f;
    public Sprite SpriteShownWhenHurt;
    private float ChangeSpriteHealth;

    // 优化：组件在 Awake 缓存，碰撞回调里不再 GetComponent
    AudioSource audioSource;
    SpriteRenderer spriteRenderer;

    void Awake()
    {
        audioSource = GetComponent<AudioSource>();
        spriteRenderer = GetComponent<SpriteRenderer>();
    }

    // Use this for initialization
    void Start()
    {
        ChangeSpriteHealth = Health - 30f;
    }

    void OnCollisionEnter2D(Collision2D col)
    {
        // 优化：直接用碰撞体自带的刚体引用
        Rigidbody2D otherBody = col.rigidbody;
        if (otherBody == null) return;

        //if we are hit by a bird
        if (col.gameObject.CompareTag("Bird"))
        {
            audioSource.Play();
            Destroy(gameObject);
        }
        else //we're hit by something else
        {
            //calculate the damage via the hit object velocity
            float damage = otherBody.velocity.magnitude * 10;
            Health -= damage;
            //don't play sound for small damage
            if (damage >= 10)
                audioSource.Play();

            if (Health < ChangeSpriteHealth)
            {//change the shown sprite
                spriteRenderer.sprite = SpriteShownWhenHurt;
            }
            if (Health <= 0) Destroy(this.gameObject);
        }
    }

    //sound found in
    //https://www.freesound.org/people/yottasounds/sounds/176731/
}
