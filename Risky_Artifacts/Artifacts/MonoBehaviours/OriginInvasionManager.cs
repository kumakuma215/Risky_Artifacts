﻿using RoR2;
using UnityEngine;
using R2API;
using UnityEngine.Networking;
using System.Collections.Generic;
using System;
using System.Collections;

namespace Risky_Artifacts.Artifacts.MonoBehaviours
{
    //Mostly based off of DoppelgangerInvasionManager
    public class OriginInvasionManager : MonoBehaviour
    {
        private int previousInvasionCycle;
        private ulong seed;
        private Run run;
        private Xoroshiro128Plus treasureRng;
        private float invasionInterval = 600f;

        public static float spawnDelay = 1.2f;
        public static int maxSpawns = 100;  //-1 disables limit
        public static int beadBossCount = 1;

        private List<DirectorSpawnRequest> pendingSpawns;

        private bool artifactIsEnabled
        {
            get
            {
                return RunArtifactManager.instance.IsArtifactEnabled(Origin.artifact);
            }
        }
        private void FixedUpdate()
        {
            int currentInvasionCycle = this.GetCurrentInvasionCycle();
            if (this.previousInvasionCycle < currentInvasionCycle)
            {
                this.previousInvasionCycle = currentInvasionCycle;
                if (this.artifactIsEnabled)
                {
                    StartCoroutine(PerformInvasion(new Xoroshiro128Plus(this.seed + (ulong)((long)currentInvasionCycle))));
                }
            }
        }
        private int GetCurrentInvasionCycle()
        {
            return Mathf.FloorToInt(this.run.GetRunStopwatch() / this.invasionInterval);
        }

        private static void OnRunStartGlobal(Run run)
        {
            if (NetworkServer.active)
            {
                run.gameObject.AddComponent<OriginInvasionManager>();
            }
        }

        public static void Init()
        {
            Run.onRunStartGlobal += OnRunStartGlobal;
        }
        private void OnEnable()
        {
            GlobalEventManager.onCharacterDeathGlobal += this.OnCharacterDeathGlobal;
            ArtifactTrialMissionController.onShellTakeDamageServer += this.OnArtifactTrialShellTakeDamageServer;
        }
        private void OnDisable()
        {
            GlobalEventManager.onCharacterDeathGlobal -= this.OnCharacterDeathGlobal;
            ArtifactTrialMissionController.onShellTakeDamageServer -= this.OnArtifactTrialShellTakeDamageServer;
        }

        private void Start()
        {
            this.run = base.GetComponent<Run>();
            this.seed = this.run.seed;
            this.treasureRng = new Xoroshiro128Plus(this.seed);
            pendingSpawns = new List<DirectorSpawnRequest>();
        }

        private void OnCharacterDeathGlobal(DamageReport damageReport)
        {
            CharacterMaster victimMaster = damageReport.victimMaster;
            Inventory inventory = (victimMaster != null) ? victimMaster.inventory : null;
            if (inventory)
            {
                if (inventory.GetItemCount(Origin.OriginBonusItem) > 0 && inventory.GetItemCount(RoR2Content.Items.ExtraLife) == 0 && !damageReport.victimMaster.minionOwnership.ownerMaster)
                {
                    Origin.DropItem(damageReport.victimBody.corePosition, treasureRng);
                }
            }
        }

        private void OnArtifactTrialShellTakeDamageServer(ArtifactTrialMissionController missionController, DamageReport damageReport)
        {
            if (!this.artifactIsEnabled)
            {
                return;
            }
            if (!damageReport.victim.alive)
            {
                return;
            }
            StartCoroutine(PerformInvasion(new Xoroshiro128Plus((ulong)damageReport.victim.health)));
        }

        IEnumerator PerformInvasion(Xoroshiro128Plus rng)
        {
            int totalSpawns = 0;

            int playerNumber = 0;

            //Select spawncard
            SpawnCard spawnCard = Origin.SelectSpawnCard(rng);
            if (spawnCard)
            {
                EliteDef selectedElite = null;
                float eliteHPMult = 1f;
                float eliteDamageMult = 1f;

                if (CombatDirector.IsEliteOnlyArtifactActive())
                {
                    CombatDirector.EliteTierDef t1Elite = CombatDirector.eliteTiers[1];
                    eliteHPMult = t1Elite.healthBoostCoefficient;
                    eliteDamageMult = t1Elite.damageBoostCoefficient;
                    selectedElite = t1Elite.eliteTypes[rng.RangeInt(0, t1Elite.eliteTypes.Length)];
                }

                for (int i = CharacterMaster.readOnlyInstancesList.Count - 1; i >= 0; i--)
                {
                    CharacterMaster characterMaster = CharacterMaster.readOnlyInstancesList[i];
                    if (characterMaster.teamIndex == TeamIndex.Player && characterMaster.playerCharacterMasterController)
                    {
                        int spawnCount = (playerNumber % 2 == 0) ? 1 : 0;
                        playerNumber++;
                        if (characterMaster.inventory)
                        {
                            spawnCount += characterMaster.inventory.GetItemCount(RoR2Content.Items.LunarTrinket) * beadBossCount;
                        }
                        //spawnCount *= 1 + (Run.instance.stageClearCount / 5);
                        for (int j = 0; j < spawnCount; j++)
                        {
                            if (!spawnCard || (maxSpawns >= 0 && totalSpawns >= maxSpawns))
                            {
                                yield return null;
                            }
                            Transform spawnOnTarget;
                            DirectorCore.MonsterSpawnDistance input;

                            spawnOnTarget = characterMaster.GetBody().coreTransform;
                            input = DirectorCore.MonsterSpawnDistance.Close;

                            DirectorPlacementRule directorPlacementRule = new DirectorPlacementRule
                            {
                                spawnOnTarget = spawnOnTarget,
                                placementMode = DirectorPlacementRule.PlacementMode.NearestNode
                            };
                            DirectorCore.GetMonsterSpawnDistance(input, out directorPlacementRule.minDistance, out directorPlacementRule.maxDistance);
                            DirectorSpawnRequest directorSpawnRequest = new DirectorSpawnRequest(spawnCard, directorPlacementRule, rng);
                            directorSpawnRequest.teamIndexOverride = new TeamIndex?(TeamIndex.Monster);
                            directorSpawnRequest.ignoreTeamMemberLimit = true;
                            CombatSquad combatSquad = null;
                            directorSpawnRequest.onSpawnedServer = (Action<SpawnCard.SpawnResult>)Delegate.Combine(directorSpawnRequest.onSpawnedServer, new Action<SpawnCard.SpawnResult>(delegate (SpawnCard.SpawnResult result)
                            {
                                if (!combatSquad)
                                {
                                    combatSquad = UnityEngine.Object.Instantiate<GameObject>(Resources.Load<GameObject>("Prefabs/NetworkedObjects/Encounters/ShadowCloneEncounter")).GetComponent<CombatSquad>();
                                }
                                combatSquad.AddMember(result.spawnedInstance.GetComponent<CharacterMaster>());
                                CharacterMaster resultMaster = result.spawnedInstance.GetComponent<CharacterMaster>();
                                if (resultMaster && resultMaster.inventory)
                                {
                                    resultMaster.inventory.GiveItem(Origin.OriginBonusItem);
                                    resultMaster.inventory.GiveItem(RoR2Content.Items.AdaptiveArmor);
                                    resultMaster.inventory.RemoveItem(RoR2Content.Items.InvadingDoppelganger);

                                    if (selectedElite != null)
                                    {
                                        resultMaster.inventory.GiveEquipmentString(selectedElite.eliteEquipmentDef.name);
                                        resultMaster.inventory.GiveItem(RoR2Content.Items.BoostHp, (int)((eliteHPMult - 1f) * 10f));
                                        resultMaster.inventory.GiveItem(RoR2Content.Items.BoostDamage, (int)((eliteDamageMult - 1f) * 10f));
                                    }
                                }
                            }));
                            DirectorCore.instance.TrySpawnObject(directorSpawnRequest);
                            if (combatSquad)
                            {
                                NetworkServer.Spawn(combatSquad.gameObject);
                                totalSpawns++;
                                yield return new WaitForSeconds(1.5f);
                            }
                        }
                    }
                }
                yield return null;
            }
        }
    }
}
