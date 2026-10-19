using System;
using System.Collections.Generic;
using HarmonyLib;
using MiSide_VR.Core;
using UnityEngine;
namespace Codex.MiSideComfort;
internal static class SubtitleLayout {
 sealed class Entry {
  internal Canvas Canvas;
  internal Vector3 AuthoredScale;
  internal readonly List<Dialogue_Symbol> Symbols=new();
  internal readonly HashSet<int> SymbolIds=new();
  internal bool Logged;
 }
 static readonly Dictionary<int,Entry> entries=new();
 static readonly List<int> expired=new();
 static bool applying,failed;
 internal static void Install(Harmony h){
  var adapter=AccessTools.TypeByName("MiSide_VR.UI.Patches.PlayerDialogueAdapter");
  h.Patch(AccessTools.Method(adapter,"Observe"),prefix:new HarmonyMethod(typeof(SubtitleLayout),nameof(ObserveEarly)));
  h.Patch(AccessTools.Method(adapter,"Apply"),prefix:new HarmonyMethod(typeof(SubtitleLayout),nameof(ReplaceOldApply)));
  h.Patch(AccessTools.Method(typeof(Dialogue_3DText),"Start"),prefix:new HarmonyMethod(typeof(SubtitleLayout),nameof(DialogueStarting)),postfix:new HarmonyMethod(typeof(SubtitleLayout),nameof(DialogueStarting)));
  h.Patch(AccessTools.Method(typeof(Dialogue_Symbol),"StartComponent"),postfix:new HarmonyMethod(typeof(SubtitleLayout),nameof(SymbolStarted)));
 }
 static Entry Register(Canvas canvas){
  if(!canvas||!canvas.gameObject.activeInHierarchy||!canvas.name.StartsWith("DPlayer",StringComparison.Ordinal))return null;
  var id=canvas.GetInstanceID();
  if(entries.TryGetValue(id,out var known))return known;
  var e=new Entry{Canvas=canvas,AuthoredScale=canvas.transform.localScale};entries.Add(id,e);
  return e;
 }
 static void ObserveEarly(Canvas canvas){if(Settings.Subtitles.Value&&!failed)Register(canvas);}
 static void DialogueStarting(Dialogue_3DText __instance){
  if(!Settings.Subtitles.Value||failed||!__instance)return;
  try{Register(__instance.GetComponentInParent<Canvas>());Apply();}
  catch(Exception e){Fail(e);}
 }
 static void SymbolStarted(Dialogue_Symbol __instance){
  if(!Settings.Subtitles.Value||failed||!__instance)return;
  try{
   var e=Register(__instance.GetComponentInParent<Canvas>());if(e==null)return;
   if(e.SymbolIds.Add(__instance.GetInstanceID()))e.Symbols.Add(__instance);
   // First-draw protection: newly spawned glyphs are normalized immediately.
   var p=VRPlayer.Instance;if(p&&p.head){Place(e,p.head);Normalize(__instance,p.head);}
  }catch(Exception e){Fail(e);}
 }
 static bool ReplaceOldApply(Transform viewer){
  if(!Settings.Subtitles.Value||failed||!viewer)return true;
  Apply();return failed;
 }
 internal static void Apply(){
  if(applying||failed||!Settings.Subtitles.Value)return;
  var p=VRPlayer.Instance;if(!p||!p.head||entries.Count==0)return;
  applying=true;
  try{
   expired.Clear();
   foreach(var pair in entries){
    var e=pair.Value;if(!e.Canvas){expired.Add(pair.Key);continue;}
    if(!e.Canvas.gameObject.activeInHierarchy)continue;
    Place(e,p.head);
    float nearest=float.MaxValue;int visible=0;
    for(int i=e.Symbols.Count-1;i>=0;i--){
     var symbol=e.Symbols[i];if(!symbol){e.Symbols.RemoveAt(i);continue;}
     if(!symbol.gameObject.activeInHierarchy)continue;
     Normalize(symbol,p.head);
     if(symbol.rect){nearest=Mathf.Min(nearest,Vector3.Dot(symbol.rect.position-p.head.position,p.head.forward));visible++;}
    }
    if(!e.Logged&&visible>0){
     e.Logged=true;
     Plugin.Logger.LogInfo("Subtitle protected before draw: "+e.Canvas.name+"; glyphs="+visible+"; nearestDepth="+nearest.ToString("F3")+"; headsetScale="+p.head.lossyScale.x.ToString("F3"));
    }
   }
   foreach(var id in expired)entries.Remove(id);
  }catch(Exception e){Fail(e);}finally{applying=false;}
 }
 static void Place(Entry e,Transform head){
  var metres=Settings.Distance.Value;var rig=Mathf.Max(.01f,Mathf.Abs(head.lossyScale.x));
  var t=e.Canvas.transform;
  t.SetPositionAndRotation(head.position+(head.forward*metres-head.up*Settings.BelowEyes.Value)*rig,head.rotation);
  t.localScale=e.AuthoredScale*(1.2f*metres*Settings.TextSize.Value);
 }
 static void Normalize(Dialogue_Symbol symbol,Transform head){
  if(!Settings.FreezeGlyphMotion.Value||!symbol||!symbol.rect)return;
  var rect=symbol.rect;
  // Preserve source lifetime, typing, audio and alpha; only remove spatial approach/jump.
  rect.localPosition=symbol.posOrigin;
  rect.localScale=Vector3.one*symbol.scaleOriginal;
  rect.localRotation=Quaternion.identity;
  var minimum=Settings.Distance.Value*.9f*Mathf.Max(.01f,Mathf.Abs(head.lossyScale.x));
  var depth=Vector3.Dot(rect.position-head.position,head.forward);
  if(depth<minimum)rect.position+=head.forward*(minimum-depth);
 }
 static void Fail(Exception e){failed=true;Plugin.Logger.LogError("Subtitle comfort disabled; original adapter restored: "+e);}
 internal static void Reset(){entries.Clear();expired.Clear();applying=failed=false;}
}
