using UnityEngine;
using System.Collections;

public class AfterParallaxScrolling : MonoBehaviour {

	// Use this for initialization
	void Start () {
        // 优化：改名为 parallaxCamera，不再遮蔽基类里已废弃的 Component.camera
        parallaxCamera = Camera.main;
        previousCameraTransform = parallaxCamera.transform.position;
	}

    Camera parallaxCamera;
	
	/// <summary>
	/// similar tactics just like the "CameraMove" script
	/// </summary>
	void Update () {
        Vector3 delta = parallaxCamera.transform.position - previousCameraTransform;
        delta.y = 0; delta.z = 0;
        transform.position += delta / ParallaxFactor;


        previousCameraTransform = parallaxCamera.transform.position;
	}

    public float ParallaxFactor;

    Vector3 previousCameraTransform;

    ///background graphics found here:
    ///http://opengameart.org/content/hd-multi-layer-parallex-background-samples-of-glitch-game-assets
}
