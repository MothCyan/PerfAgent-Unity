using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Assets.Scripts
{
    //enums for the state of the slingshot, the 
    //state of the game and the state of the bird
    public enum BeforeSlingshotState
    {
        Idle,
        UserPulling,
        BirdFlying
    }

    public enum BeforeGameState
    {
        Start,
        BirdMovingToSlingshot,
        Playing,
        Won,
        Lost
    }


    public enum BeforeBirdState
    {
        BeforeThrown,
        Thrown
    }
    
}
