using System;
using System.Reflection;
using HarmonyLib;
using MiSide_VR.Core;
using UnityEngine;
namespace Codex.MiSideComfort;
internal static class CinematicComfort {
 static readonly CinematicMaskPolicy policy=new();
 static FieldInfo trackingYaw,trackingValid,consumedYaw,animationYawActive,animationLastYaw;
 static bool yawLocked,hasCameraPose,maskLogged;
 static float lockedYaw,lastEventLog=-10f;
 static int lastFrame=-1;
 static Vector3 previousPosition;
 static Quaternion previousRotation;
 internal static float MaskAlpha { get; private set; }
 internal static void Install(Harmony h){
  var t=typeof(VRPlayer);
  trackingYaw=AccessTools.Field(t,"_playerTrackingYaw");trackingValid=AccessTools.Field(t,"_playerTrackingYawValid");
  consumedYaw=AccessTools.Field(t,"_roomscaleConsumedYaw");animationYawActive=AccessTools.Field(t,"_playerAnimationYawActive");animationLastYaw=AccessTools.Field(t,"_playerAnimationLastSourceYaw");
  if(trackingYaw==null||trackingValid==null||consumedYaw==null||animationYawActive==null||animationLastYaw==null)throw new InvalidOperationException("VR tracking-yaw fields changed");
  h.Patch(AccessTools.Method(t,"GetPlayerTrackingYaw"),prefix:new HarmonyMethod(typeof(CinematicComfort),nameof(TrackingYawPrefix)));
  foreach(var method in AccessTools.GetDeclaredMethods(typeof(PlayerMove))){
   // TeleportRotate is intentionally excluded: VR uses it for USER snap/roomscale turns.
   if(method.Name is "AnimationPlay" or "AnimationCameraPlay" or "Look" or "CameraTargetOther" or "TeleportPlayer" or "TeleportPlayerGeneral")
    h.Patch(method,prefix:new HarmonyMethod(typeof(CinematicComfort),nameof(StoryTakeover)));
  }
 }
 static void StoryTakeover(PlayerMove __instance,MethodBase __originalMethod){
  var p=VRPlayer.Instance;if(!p||!p.playerMove||p.playerMove!=__instance||p.mode==GameMode.MenuOrLoading)return;
  policy.Begin(Time.unscaledTime);
  if(Time.unscaledTime-lastEventLog>.5f){lastEventLog=Time.unscaledTime;Plugin.Logger.LogInfo("Scripted camera event: "+__originalMethod.Name);}
 }
 static bool StoryOwnsCamera(VRPlayer p){
  if(!p||p.mode is GameMode.MenuOrLoading or GameMode.Novella)return false;
  if(p.mode==GameMode.PlayerAnimation)return true;
  if(p.mode!=GameMode.StandardPlayer)return false;
  var a=p.playerMove;
  return a&&(a.animationRun||a.animationCameraActive||(a.lookTarget&&a.lookTime>0f)||a.cameraOtherTarget);
 }
 static bool TrackingYawPrefix(VRPlayer __instance,float sourceYaw,ref float __result){
  if(!Settings.LockScriptedYaw.Value)return true;
  var story=StoryOwnsCamera(__instance);
  if(story&&(bool)trackingValid.GetValue(__instance)){
   if(!yawLocked){lockedYaw=(float)trackingYaw.GetValue(__instance);yawLocked=true;Plugin.Logger.LogInfo("Scripted yaw guarded; physical HMD rotation remains live.");}
   trackingYaw.SetValue(__instance,lockedYaw);
   animationLastYaw.SetValue(__instance,sourceYaw);
   __result=lockedYaw;return false;
  }
  if(yawLocked){
   // Carry the preserved heading into ordinary control; no release-time camera snap.
   consumedYaw.SetValue(__instance,Mathf.DeltaAngle(lockedYaw,sourceYaw));
   trackingYaw.SetValue(__instance,lockedYaw);trackingValid.SetValue(__instance,true);
   animationYawActive.SetValue(__instance,false);animationLastYaw.SetValue(__instance,sourceYaw);
   yawLocked=false;__result=lockedYaw;Plugin.Logger.LogInfo("Scripted yaw released with heading rebased.");return false;
  }
  return true;
 }
 internal static void Tick(){
  if(lastFrame==Time.frameCount)return;lastFrame=Time.frameCount;
  var p=VRPlayer.Instance;var source=GameContext.SourceCamera;
  if(!p||p.mode is GameMode.MenuOrLoading or GameMode.Novella){policy.Reset();MaskAlpha=0;hasCameraPose=false;return;}
  var moved=false;var abrupt=false;
  if(source){
   var pos=source.transform.position;var rot=source.transform.rotation;
   if(hasCameraPose){
    var distanceSquared=(pos-previousPosition).sqrMagnitude;var angle=Quaternion.Angle(rot,previousRotation);
    moved=distanceSquared>0.000001f||angle>.08f;abrupt=distanceSquared>=.0225f||angle>=10f;
   }
   previousPosition=pos;previousRotation=rot;hasCameraPose=true;
  }else hasCameraPose=false;
  MaskAlpha=Settings.CinematicMask.Value?policy.Step(Time.unscaledTime,Time.unscaledDeltaTime,StoryOwnsCamera(p),moved,Settings.FadeIn.Value,Settings.FadeOut.Value,abrupt):0;
  if(MaskAlpha>.05f&&!maskLogged){maskLogged=true;Plugin.Logger.LogInfo("Cinematic peripheral mask engaged (authored camera takeover).");}
  if(MaskAlpha==0&&maskLogged){maskLogged=false;Plugin.Logger.LogInfo("Cinematic peripheral mask released.");}
 }
 internal static void Reset(){policy.Reset();MaskAlpha=0;yawLocked=hasCameraPose=maskLogged=false;lastFrame=-1;}
}
