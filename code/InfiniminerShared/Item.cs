using System;
using System.Collections.Generic;
using System.Text;
using Lidgren.Network;
using Lidgren.Network.Xna;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Audio;
using Microsoft.Xna.Framework.Content;
using Microsoft.Xna.Framework.GamerServices;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using Microsoft.Xna.Framework.Media;
using Microsoft.Xna.Framework.Net;
using Microsoft.Xna.Framework.Storage;

namespace Infiniminer
{
    public class Item
    {
        public string ID;
        public PlayerTeam Team;
        public Vector3 Heading;
        public int Type;

        public bool QueueAnimationBreak = false;

        // Things that affect animation.
        public SpriteModel SpriteModel;
        private Game gameInstance;

        public Item(Game gameInstance)
        {
            this.gameInstance = gameInstance;
            if (gameInstance != null)
            {
                this.SpriteModel = new SpriteModel(gameInstance, 4);
                UpdateSpriteTexture();
                this.IdleAnimation = true;
            }
        }

        private bool idleAnimation = false;
        public bool IdleAnimation
        {
            get { return idleAnimation; }
            set
            {
                if (idleAnimation != value)
                {
                    idleAnimation = value;
                    if (gameInstance != null)
                    {
                        if (idleAnimation)
                            SpriteModel.SetPassiveAnimation("1,0.2");
                        else
                            SpriteModel.SetPassiveAnimation("0,0.2;1,0.2;2,0.2;1,0.2");
                    }
                }
            }
        }

        private void UpdateSpriteTexture()
        {
            if (gameInstance == null)
                return;

            string textureName = "sprites/tex_sprite_lemonorgoldnum";
    
            Texture2D orig = gameInstance.Content.Load<Texture2D>(textureName);
           
            this.SpriteModel.SetSpriteTexture(orig);
        }
    }
}