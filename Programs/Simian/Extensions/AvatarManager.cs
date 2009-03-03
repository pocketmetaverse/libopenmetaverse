using System;
using System.Collections.Generic;
using System.Threading;
using ExtensionLoader;
using OpenMetaverse;
using OpenMetaverse.StructuredData;
using OpenMetaverse.Packets;

namespace Simian.Extensions
{
    class AvatarManager : IExtension<Simian>, IAvatarProvider
    {
        Simian server;
        int currentWearablesSerialNum = -1;
        int currentAnimSequenceNum = 0;
        Timer CoarseLocationTimer;

        public AvatarManager()
        {
        }

        public void Start(Simian server)
        {
            this.server = server;

            server.UDP.RegisterPacketCallback(PacketType.AvatarPropertiesRequest, AvatarPropertiesRequestHandler);
            server.UDP.RegisterPacketCallback(PacketType.AgentWearablesRequest, AgentWearablesRequestHandler);
            server.UDP.RegisterPacketCallback(PacketType.AgentIsNowWearing, AgentIsNowWearingHandler);
            server.UDP.RegisterPacketCallback(PacketType.AgentSetAppearance, AgentSetAppearanceHandler);
            server.UDP.RegisterPacketCallback(PacketType.AgentCachedTexture, AgentCachedTextureHandler);
            server.UDP.RegisterPacketCallback(PacketType.AgentHeightWidth, AgentHeightWidthHandler);
            server.UDP.RegisterPacketCallback(PacketType.AgentAnimation, AgentAnimationHandler);
            server.UDP.RegisterPacketCallback(PacketType.SoundTrigger, SoundTriggerHandler);
            server.UDP.RegisterPacketCallback(PacketType.ViewerEffect, ViewerEffectHandler);
            server.UDP.RegisterPacketCallback(PacketType.UUIDNameRequest, UUIDNameRequestHandler);

            if (CoarseLocationTimer != null) CoarseLocationTimer.Dispose();
            CoarseLocationTimer = new Timer(CoarseLocationTimer_Elapsed);
            CoarseLocationTimer.Change(1000, 1000);
        }

        public void Stop()
        {
            if (CoarseLocationTimer != null)
            {
                CoarseLocationTimer.Dispose();
                CoarseLocationTimer = null;
            }
        }

        public bool SetDefaultAnimation(Agent agent, UUID animID)
        {
            return agent.Animations.SetDefaultAnimation(animID, ref currentAnimSequenceNum);
        }

        public bool AddAnimation(Agent agent, UUID animID)
        {
            return agent.Animations.Add(animID, ref currentAnimSequenceNum);
        }

        public bool RemoveAnimation(Agent agent, UUID animID)
        {
            return agent.Animations.Remove(animID);
        }

        public bool ClearAnimations(Agent agent)
        {
            agent.Animations.Clear();
            return true;
        }

        public void SendAnimations(Agent agent)
        {
            server.Scene.ObjectAnimate(this, agent.ID, agent.ID, agent.Animations.GetAnimations());
        }

        public void TriggerSound(Agent agent, UUID soundID, float gain)
        {
            server.Scene.TriggerSound(this, agent.ID, agent.ID, agent.ID, soundID, agent.Avatar.Prim.Position, gain);
        }

        public void SendAlert(Agent agent, string message)
        {
            AlertMessagePacket alert = new AlertMessagePacket();
            alert.AlertData.Message = Utils.StringToBytes(message);
            server.UDP.SendPacket(agent.ID, alert, PacketCategory.Transaction);
        }

        void AgentAnimationHandler(Packet packet, Agent agent)
        {
            AgentAnimationPacket animPacket = (AgentAnimationPacket)packet;
            bool changed = false;

            for (int i = 0; i < animPacket.AnimationList.Length; i++)
            {
                AgentAnimationPacket.AnimationListBlock block = animPacket.AnimationList[i];

                if (block.StartAnim)
                {
                    if (agent.Animations.Add(block.AnimID, ref currentAnimSequenceNum))
                        changed = true;
                }
                else
                {
                    if (agent.Animations.Remove(block.AnimID))
                        changed = true;
                }
            }

            if (changed)
                SendAnimations(agent);
        }

        void ViewerEffectHandler(Packet packet, Agent agent)
        {
            ViewerEffectPacket incomingEffects = (ViewerEffectPacket)packet;

            ViewerEffect[] outgoingEffects = new ViewerEffect[incomingEffects.Effect.Length];

            for (int i = 0; i < outgoingEffects.Length; i++)
            {
                ViewerEffectPacket.EffectBlock block = incomingEffects.Effect[i];
                outgoingEffects[i] = new ViewerEffect(block.ID, (EffectType)block.Type, block.AgentID,
                    new Color4(block.Color, 0, true), block.Duration, block.TypeData);
            }

            server.Scene.TriggerEffects(this, outgoingEffects);
        }

        void AvatarPropertiesRequestHandler(Packet packet, Agent agent)
        {
            AvatarPropertiesRequestPacket request = (AvatarPropertiesRequestPacket)packet;

            Agent foundAgent;
            if (server.Scene.TryGetAgent(request.AgentData.AvatarID, out foundAgent))
            {
                AvatarPropertiesReplyPacket reply = new AvatarPropertiesReplyPacket();
                reply.AgentData.AgentID = agent.ID;
                reply.AgentData.AvatarID = request.AgentData.AvatarID;
                reply.PropertiesData.AboutText = Utils.StringToBytes(foundAgent.ProfileAboutText);
                reply.PropertiesData.BornOn = Utils.StringToBytes(foundAgent.ProfileBornOn);
                reply.PropertiesData.CharterMember = new byte[1];
                reply.PropertiesData.FLAboutText = Utils.StringToBytes(foundAgent.ProfileFirstText);
                reply.PropertiesData.Flags = (uint)foundAgent.ProfileFlags;
                reply.PropertiesData.FLImageID = foundAgent.ProfileFirstImage;
                reply.PropertiesData.ImageID = foundAgent.ProfileImage;
                reply.PropertiesData.PartnerID = foundAgent.PartnerID;
                reply.PropertiesData.ProfileURL = Utils.StringToBytes(foundAgent.ProfileURL);

                server.UDP.SendPacket(agent.ID, reply, PacketCategory.Transaction);
            }
            else
            {
                Logger.Log("AvatarPropertiesRequest for unknown agent " + request.AgentData.AvatarID.ToString(),
                    Helpers.LogLevel.Warning);
            }
        }

        bool TryAddWearable(UUID agentID, Dictionary<WearableType, InventoryItem> wearables, WearableType type, UUID itemID)
        {
            InventoryObject obj;
            if (itemID != UUID.Zero && server.Inventory.TryGetInventory(agentID, itemID, out obj) &&
                obj is InventoryItem)
            {
                wearables.Add(type, (InventoryItem)obj);
                return true;
            }
            else
            {
                return false;
            }
        }

        Dictionary<WearableType, InventoryItem> GetCurrentWearables(Agent agent)
        {
            Dictionary<WearableType, InventoryItem> wearables = new Dictionary<WearableType, InventoryItem>();

            TryAddWearable(agent.ID, wearables, WearableType.Shape, agent.ShapeItem);
            TryAddWearable(agent.ID, wearables, WearableType.Skin, agent.SkinItem);
            TryAddWearable(agent.ID, wearables, WearableType.Hair, agent.HairItem);
            TryAddWearable(agent.ID, wearables, WearableType.Eyes, agent.EyesItem);
            TryAddWearable(agent.ID, wearables, WearableType.Shirt, agent.ShirtItem);
            TryAddWearable(agent.ID, wearables, WearableType.Pants, agent.PantsItem);
            TryAddWearable(agent.ID, wearables, WearableType.Shoes, agent.ShoesItem);
            TryAddWearable(agent.ID, wearables, WearableType.Socks, agent.SocksItem);
            TryAddWearable(agent.ID, wearables, WearableType.Jacket, agent.JacketItem);
            TryAddWearable(agent.ID, wearables, WearableType.Gloves, agent.GlovesItem);
            TryAddWearable(agent.ID, wearables, WearableType.Undershirt, agent.UndershirtItem);
            TryAddWearable(agent.ID, wearables, WearableType.Underpants, agent.UnderpantsItem);
            TryAddWearable(agent.ID, wearables, WearableType.Skirt, agent.SkirtItem);

            return wearables;
        }

        void SendWearables(Agent agent)
        {
            AgentWearablesUpdatePacket update = new AgentWearablesUpdatePacket();
            update.AgentData.AgentID = agent.ID;

            Dictionary<WearableType, InventoryItem> wearables = GetCurrentWearables(agent);
            update.WearableData = new AgentWearablesUpdatePacket.WearableDataBlock[wearables.Count];
            int i = 0;

            foreach (KeyValuePair<WearableType, InventoryItem> kvp in wearables)
            {
                update.WearableData[i] = new AgentWearablesUpdatePacket.WearableDataBlock();
                update.WearableData[i].AssetID = kvp.Value.AssetID;
                update.WearableData[i].ItemID = kvp.Value.ID;
                update.WearableData[i].WearableType = (byte)kvp.Key;
                ++i;
            }

            // Technically this should be per-agent, but if the only requirement is that it
            // increments this is easier
            update.AgentData.SerialNum = (uint)Interlocked.Increment(ref currentWearablesSerialNum);

            Logger.DebugLog(String.Format("Sending info about {0} wearables", wearables.Count));

            server.UDP.SendPacket(agent.ID, update, PacketCategory.Asset);
        }

        void AgentWearablesRequestHandler(Packet packet, Agent agent)
        {
            SendWearables(agent);
        }

        void AgentIsNowWearingHandler(Packet packet, Agent agent)
        {
            AgentIsNowWearingPacket wearing = (AgentIsNowWearingPacket)packet;

            for (int i = 0; i < wearing.WearableData.Length; i++)
            {
                UUID itemID = wearing.WearableData[i].ItemID;

                #region Update Wearables

                switch ((WearableType)wearing.WearableData[i].WearableType)
                {
                    case WearableType.Shape:
                        agent.ShapeItem = itemID;
                        break;
                    case WearableType.Skin:
                        agent.SkinItem = itemID;
                        break;
                    case WearableType.Hair:
                        agent.HairItem = itemID;
                        break;
                    case WearableType.Eyes:
                        agent.EyesItem = itemID;
                        break;
                    case WearableType.Shirt:
                        agent.ShirtItem = itemID;
                        break;
                    case WearableType.Pants:
                        agent.PantsItem = itemID;
                        break;
                    case WearableType.Shoes:
                        agent.ShoesItem = itemID;
                        break;
                    case WearableType.Socks:
                        agent.SocksItem = itemID;
                        break;
                    case WearableType.Jacket:
                        agent.JacketItem = itemID;
                        break;
                    case WearableType.Gloves:
                        agent.GlovesItem = itemID;
                        break;
                    case WearableType.Undershirt:
                        agent.UndershirtItem = itemID;
                        break;
                    case WearableType.Underpants:
                        agent.UnderpantsItem = itemID;
                        break;
                    case WearableType.Skirt:
                        agent.SkirtItem = itemID;
                        break;
                }

                #endregion Update Wearables
            }

            // FIXME: GetCurrentWearables() is a very expensive call, remove it from this debug line
            Logger.DebugLog("Updated agent wearables, new count: " + GetCurrentWearables(agent).Count);
        }

        void AgentSetAppearanceHandler(Packet packet, Agent agent)
        {
            AgentSetAppearancePacket set = (AgentSetAppearancePacket)packet;

            Logger.DebugLog("Updating avatar appearance");

            //TODO: Store this for cached bake responses
            for (int i = 0; i < set.WearableData.Length; i++)
            {
                AppearanceManager.TextureIndex index = (AppearanceManager.TextureIndex)set.WearableData[i].TextureIndex;
                UUID cacheID = set.WearableData[i].CacheID;

                Logger.DebugLog(String.Format("WearableData: {0} is now {1}", index, cacheID));
            }

            // Create a TextureEntry
            Primitive.TextureEntry textureEntry = new Primitive.TextureEntry(set.ObjectData.TextureEntry, 0,
                set.ObjectData.TextureEntry.Length);

            // Create a block of VisualParams
            byte[] visualParams = new byte[set.VisualParam.Length];
            for (int i = 0; i < set.VisualParam.Length; i++)
                visualParams[i] = set.VisualParam[i].ParamValue;

            server.Scene.AgentAppearance(this, agent, textureEntry, visualParams);
        }

        void AgentCachedTextureHandler(Packet packet, Agent agent)
        {
            AgentCachedTexturePacket cached = (AgentCachedTexturePacket)packet;

            AgentCachedTextureResponsePacket response = new AgentCachedTextureResponsePacket();
            response.AgentData.AgentID = agent.ID;
            response.AgentData.SerialNum = cached.AgentData.SerialNum;

            response.WearableData = new AgentCachedTextureResponsePacket.WearableDataBlock[cached.WearableData.Length];

            // TODO: Respond back with actual cache entries if we have them
            for (int i = 0; i < cached.WearableData.Length; i++)
            {
                response.WearableData[i] = new AgentCachedTextureResponsePacket.WearableDataBlock();
                response.WearableData[i].TextureIndex = cached.WearableData[i].TextureIndex;
                response.WearableData[i].TextureID = UUID.Zero;
                response.WearableData[i].HostName = new byte[0];
            }

            response.Header.Zerocoded = true;

            server.UDP.SendPacket(agent.ID, response, PacketCategory.Transaction);
        }

        void AgentHeightWidthHandler(Packet packet, Agent agent)
        {
            //AgentHeightWidthPacket heightWidth = (AgentHeightWidthPacket)packet;

            // TODO: These are the screen size dimensions. Useful when we start doing frustum culling
            //Logger.Log(String.Format("Agent wants to set height={0}, width={1}",
            //    heightWidth.HeightWidthBlock.Height, heightWidth.HeightWidthBlock.Width), Helpers.LogLevel.Info);
        }

        void SoundTriggerHandler(Packet packet, Agent agent)
        {
            SoundTriggerPacket trigger = (SoundTriggerPacket)packet;
            TriggerSound(agent, trigger.SoundData.SoundID, trigger.SoundData.Gain);
        }

        void UUIDNameRequestHandler(Packet packet, Agent agent)
        {
            UUIDNameRequestPacket request = (UUIDNameRequestPacket)packet;

            UUIDNameReplyPacket reply = new UUIDNameReplyPacket();
            reply.UUIDNameBlock = new UUIDNameReplyPacket.UUIDNameBlockBlock[request.UUIDNameBlock.Length];

            for (int i = 0; i < request.UUIDNameBlock.Length; i++)
            {
                UUID id = request.UUIDNameBlock[i].ID;

                reply.UUIDNameBlock[i] = new UUIDNameReplyPacket.UUIDNameBlockBlock();
                reply.UUIDNameBlock[i].ID = id;

                Agent foundAgent;
                if (server.Scene.TryGetAgent(id, out foundAgent))
                {
                    reply.UUIDNameBlock[i].FirstName = Utils.StringToBytes(foundAgent.FirstName);
                    reply.UUIDNameBlock[i].LastName = Utils.StringToBytes(foundAgent.LastName);
                }
                else
                {
                    reply.UUIDNameBlock[i].FirstName = new byte[0];
                    reply.UUIDNameBlock[i].LastName = new byte[0];
                }
            }

            server.UDP.SendPacket(agent.ID, reply, PacketCategory.Transaction);
        }

        void CoarseLocationTimer_Elapsed(object sender)
        {
            // Create lists containing all of the agent blocks
            List<CoarseLocationUpdatePacket.AgentDataBlock> agentDatas = new List<CoarseLocationUpdatePacket.AgentDataBlock>();
            List<CoarseLocationUpdatePacket.LocationBlock> agentLocations = new List<CoarseLocationUpdatePacket.LocationBlock>();

            server.Scene.ForEachAgent(
                delegate(Agent agent)
                {
                    CoarseLocationUpdatePacket.AgentDataBlock dataBlock = new CoarseLocationUpdatePacket.AgentDataBlock();
                    dataBlock.AgentID = agent.ID;
                    CoarseLocationUpdatePacket.LocationBlock locationBlock = new CoarseLocationUpdatePacket.LocationBlock();
                    locationBlock.X = (byte)agent.Avatar.Prim.Position.X;
                    locationBlock.Y = (byte)agent.Avatar.Prim.Position.Y;
                    locationBlock.Z = (byte)((int)agent.Avatar.Prim.Position.Z / 4);

                    agentDatas.Add(dataBlock);
                    agentLocations.Add(locationBlock);
                }
            );

            // Send location updates out to each agent
            server.Scene.ForEachAgent(
                delegate(Agent agent)
                {
                    CoarseLocationUpdatePacket update = new CoarseLocationUpdatePacket();
                    update.Index.Prey = -1;
                    update.Index.You = 0;

                    // Count the number of blocks to send out
                    int count = 0;
                    for (int i = 0; i < agentDatas.Count; i++)
                    {
                        if (agentDatas[i].AgentID != agent.ID)
                            ++count;
                    }

                    update.AgentData = new CoarseLocationUpdatePacket.AgentDataBlock[count];
                    update.Location = new CoarseLocationUpdatePacket.LocationBlock[count];

                    int j = 0;
                    for (int i = 0; i < agentDatas.Count; i++)
                    {
                        if (agentDatas[i].AgentID != agent.ID)
                        {
                            update.AgentData[j] = agentDatas[i];
                            update.Location[j] = agentLocations[i];
                            ++j;
                        }
                    }

                    server.UDP.SendPacket(agent.ID, update, PacketCategory.State);
                }
            );
        }
    }
}
