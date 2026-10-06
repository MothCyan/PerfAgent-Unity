using UnityEngine;
using System.Collections;

public class Destroyer : MonoBehaviour {


    void OnTriggerEnter2D(Collider2D col)
    {
        //destroyers are located in the borders of the screen
        //if something collides with them, the'll destroy it
        //优化：CompareTag 直接走原生比较，不用先取出 tag 字符串再比
        if (col.CompareTag("Bird") || col.CompareTag("Pig") || col.CompareTag("Brick"))
        {
            Destroy(col.gameObject);
        }
    }
}
