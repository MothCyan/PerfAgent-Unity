using UnityEngine;
using System.Collections;

public class Brick : MonoBehaviour
{
    // 优化：自身组件在 Awake 缓存一次，碰撞回调里不再 GetComponent
    AudioSource audioSource;

    void Awake()
    {
        audioSource = GetComponent<AudioSource>();
    }

    void OnCollisionEnter2D(Collision2D col)
    {
        // 优化：col.rigidbody 就是撞过来的那个刚体，等价于 col.gameObject.GetComponent<Rigidbody2D>()
        Rigidbody2D otherBody = col.rigidbody;
        if (otherBody == null) return;

        float damage = otherBody.velocity.magnitude * 10;
        //don't play audio for small damages
        if (damage >= 10)
            audioSource.Play();
        //decrease health according to magnitude of the object that hit us
        Health -= damage;
        //if health is 0, destroy the block
        if (Health <= 0) Destroy(this.gameObject);
    }

    public float Health = 70f;


    //wood sound found in 
    //https://www.freesound.org/people/Srehpog/sounds/31623/
}
