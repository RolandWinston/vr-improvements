using System;
namespace Codex.MiSideComfort;
// No HMD or joystick input enters this gate: only authored camera ownership/motion.
public sealed class CinematicMaskPolicy {
 public float Alpha { get; private set; }
 float eventUntil=-1, holdUntil=-1;
 public void Begin(float now){eventUntil=Math.Max(eventUntil,now+0.25f);}
 public float Step(float now,float delta,bool storyOwnsCamera,bool authoredCameraMoved,float fadeIn,float fadeOut,bool abruptScriptedChange=false){
  var requested=(now<eventUntil||storyOwnsCamera)&&authoredCameraMoved;
  if(requested)holdUntil=now+0.16f;
  if(requested&&abruptScriptedChange)Alpha=1f;
  var target=requested||now<holdUntil?1f:0f;
  var step=Math.Max(0f,Math.Min(delta,0.1f))/Math.Max(0.02f,target>Alpha?fadeIn:fadeOut);
  Alpha=target>Alpha?Math.Min(target,Alpha+step):Math.Max(target,Alpha-step);
  return Alpha;
 }
 public void Reset(){Alpha=0;eventUntil=holdUntil=-1;}
}
