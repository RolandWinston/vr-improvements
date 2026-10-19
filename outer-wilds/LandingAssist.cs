using System;
using System.Reflection;
using HarmonyLib;
using OWML.Common;
using OWML.ModHelper;
using UnityEngine;
namespace LocalOuterVR {
 public sealed class LandingAssist : ModBehaviour {
  internal static LandingAssist Instance;
  const string PatchId="Local.OuterVRLandingAssist";
  Harmony harmony; bool active; bool previousLandingView;
  OWRigidbody ship,planet; ThrusterModel thrusters; Autopilot autopilot;
  ShipResources resources; LandingPadManager pads; LandingPadSensor[] sensors;
  ReferenceFrame target; readonly RaycastHit[] hits=new RaycastHit[32];
  float sessionStart; int lastSample; float nextHint;
  static readonly AstroObject.Name[] Allowed={AstroObject.Name.TimberHearth,AstroObject.Name.TimberMoon,AstroObject.Name.BrittleHollow,AstroObject.Name.TowerTwin,AstroObject.Name.CaveTwin};
  void Start(){
   Instance=this;harmony=new Harmony(PatchId);harmony.PatchAll(typeof(LandingAssist).Assembly);
   LoadManager.OnCompleteSceneLoad+=SceneLoaded;
   LoadManager.OnStartSceneLoad+=SceneLeaving;
   Log("READY 0.1.0; autopilot action near ground; cancellation and physics hooks installed");
  }
  void OnDestroy(){
   Stop("",false);LoadManager.OnCompleteSceneLoad-=SceneLoaded;LoadManager.OnStartSceneLoad-=SceneLeaving;
   if(harmony!=null)harmony.UnpatchSelf();if(Instance==this)Instance=null;
  }
  void SceneLeaving(OWScene from,OWScene to){Stop("",false);ClearReferences();}
  void SceneLoaded(OWScene from,OWScene to){
   Stop("",false);ClearReferences();if(to!=OWScene.SolarSystem)return;
   ship=Locator.GetShipBody();if(ship==null)return;
   thrusters=ship.GetComponent<ThrusterModel>();autopilot=ship.GetComponent<Autopilot>();
   pads=ship.GetComponent<LandingPadManager>();resources=ship.GetComponent<ShipResources>();
   sensors=ship.GetComponentsInChildren<LandingPadSensor>();
   Log("SCENE_READY ship="+(ship!=null)+" thrust="+(thrusters!=null)+" pads="+(sensors==null?0:sensors.Length)+" resources="+(resources!=null));
  }
  void ClearReferences(){ship=null;planet=null;thrusters=null;autopilot=null;pads=null;resources=null;sensors=null;target=null;active=false;}
  bool Seated(){return PlayerState.AtFlightConsole()&&OWInput.IsInputMode((InputMode)384);}
  bool Powered(){return resources!=null&&resources.AreThrustersUsable()&&resources.GetFuel()>0.1f;}
  bool Manual(){
   return Mathf.Abs(OWInput.GetValue(InputLibrary.thrustX))>0.15f || Mathf.Abs(OWInput.GetValue(InputLibrary.thrustZ))>0.15f ||
    OWInput.GetValue(InputLibrary.thrustUp)>0.15f || OWInput.GetValue(InputLibrary.thrustDown)>0.15f ||
    OWInput.GetAxisValue(InputLibrary.look).sqrMagnitude>0.0225f || OWInput.IsPressed(InputLibrary.matchVelocity) || OWInput.IsNewlyPressed(InputLibrary.cancel);
  }
  bool ResolveTarget(out ReferenceFrame frame,out OWRigidbody body){
   frame=Locator.GetReferenceFrame(true);body=null;if(frame==null)return false;
   foreach(var name in Allowed){var astro=Locator.GetAstroObject(name);if(astro==null)continue;var rb=astro.GetOWRigidbody();if(rb!=null&&rb.GetReferenceFrame()==frame){body=rb;return true;}}
   return false;
  }
  bool Ground(OWRigidbody body,out float clearance,out float slope,out Vector3 down){
   clearance=float.PositiveInfinity;slope=90;down=Vector3.zero;
   if(ship==null||body==null||sensors==null||sensors.Length==0)return false;
   Vector3 center=ship.GetWorldCenterOfMass();down=(body.GetWorldCenterOfMass()-center).normalized;if(down.sqrMagnitude<0.5f)return false;
   int count=Physics.RaycastNonAlloc(center,down,hits,145f,OWLayerMask.physicalMask,QueryTriggerInteraction.Ignore);
   if(count==hits.Length){clearance=0;return false;} // no safe ordering when the buffer overflows
   float nearest=float.PositiveInfinity;int index=-1;
   for(int i=0;i<count;i++){
    var col=hits[i].collider;if(col==null||col.transform.IsChildOf(ship.transform))continue;
    if(hits[i].distance<nearest){nearest=hits[i].distance;index=i;}
   }
   if(index<0)return false;
   var hit=hits[index];
   float gearDrop=0;foreach(var sensor in sensors){if(sensor!=null)gearDrop=Mathf.Max(gearDrop,Vector3.Dot(sensor.transform.position-center,down));}
   clearance=Mathf.Max(0,hit.distance-gearDrop-0.25f);slope=Vector3.Angle(hit.normal,-down);
   if(!hit.collider.transform.IsChildOf(body.transform))return false;
   return LandingPolicy.Finite(clearance)&&clearance<=120&&slope<=30;
  }
  internal void CockpitInput(){
   if(ship==null||autopilot==null||pads==null||thrusters==null)return;
   if(active&&(!PlayerState.AtFlightConsole()||PlayerState.IsDead())){Stop("自动降落已结束，手动控制已恢复。",true);return;}
   if(!Seated()||Time.timeScale<=0)return;
   bool pressed=OWInput.IsNewlyPressed(InputLibrary.autopilot);
   if(active){
    if(pressed){InputLibrary.autopilot.ConsumeInput();Stop("已取消自动降落。",true);return;}
    if(Manual())Stop("已交还手动控制。",true);
    return;
   }
   if(!pressed||autopilot.IsFlyingToDestination()||pads.IsLanded())return;
   ReferenceFrame frame;OWRigidbody body;if(!ResolveTarget(out frame,out body))return;
   float clearance,slope;Vector3 down;if(!Ground(body,out clearance,out slope,out down)){
    if(clearance<=120){InputLibrary.autopilot.ConsumeInput();Hint("地面过陡或有障碍，降落辅助未接管。");}return;
   }
   // A near-ground action is handled here even if a safety gate refuses engagement.
   InputLibrary.autopilot.ConsumeInput();
   Vector3 relative=ship.GetVelocity()-body.GetPointVelocity(ship.GetWorldCenterOfMass());
   float speed=relative.magnitude;
   if(!LandingPolicy.CanStart(Seated(),!PlayerState.IsDead(),Powered(),Manual(),true,clearance,speed,slope)||!LandingPolicy.CanBrake(clearance,Vector3.Dot(relative,down),thrusters.GetMaxTranslationalThrust())){
    Hint("先匹配速度并松开摇杆、扳机和 A，再按右摇杆降落。");return;
   }
   target=frame;planet=body;previousLandingView=PlayerState.InLandingView();
   autopilot.Abort();autopilot.StopMatchVelocity();active=true;sessionStart=Time.time;lastSample=-1;
   GlobalMessenger<ReferenceFrame>.FireEvent("EnterLandingMode",target);
   Hint("自动降落中：再次按右摇杆，或推动摇杆/扳机即可取消。");
   Log("ENGAGED clearance="+clearance.ToString("F2")+" speed="+speed.ToString("F2")+" target="+planet.name);
  }
  internal void Translation(ref Vector3 input){
   if(!active)return;
   bool contact=pads!=null&&(pads.IsLanded()||pads.GetContactCount()>0);
   if(contact){Stop("已接触地面，控制已交还。",true);return;}
   if(Time.timeScale<=0)return;
   ReferenceFrame frame;OWRigidbody body;bool same=ResolveTarget(out frame,out body)&&frame==target&&body==planet;
   float clearance,slope;Vector3 down;bool ground=Ground(planet,out clearance,out slope,out down);
   bool manual=Manual();
   if(!LandingPolicy.Continue(Seated(),!PlayerState.IsDead(),Powered(),manual,same,ground,contact)){
    Stop(manual?"已交还手动控制。":"降落辅助已停止，请手动接管。",true);return;
   }
   if(Time.time-sessionStart>90f){Stop("降落辅助超时，请手动接管。",true);return;}
   Vector3 relative=ship.GetVelocity()-planet.GetPointVelocity(ship.GetWorldCenterOfMass());
   float horizontal=Vector3.ProjectOnPlane(relative,down).magnitude;
   float tilt=Vector3.Angle(ship.transform.up,-down);
   float descent=LandingPolicy.DescentSpeed(clearance,horizontal,tilt);
   Vector3 error=down*descent-relative;
   float magnitude=LandingPolicy.Demand(error.magnitude,thrusters.GetMaxTranslationalThrust(),Time.fixedDeltaTime);
   input=ship.transform.InverseTransformDirection(error.sqrMagnitude>0.000001f?error.normalized*magnitude:Vector3.zero);
   int sample=(int)((Time.time-sessionStart)/5f);if(sample!=lastSample){lastSample=sample;Log("DESCENT height="+clearance.ToString("F2")+" speed="+relative.magnitude.ToString("F2")+" lateral="+horizontal.ToString("F2")+" tilt="+tilt.ToString("F1"));}
  }
  void Update(){if(active&&(!PlayerState.AtFlightConsole()||PlayerState.IsDead()||ship==null))Stop("",false);}
  void Stop(string message,bool notify){
   if(!active)return;active=false;
   if(!previousLandingView&&!PlayerState.InLandingView())GlobalMessenger.FireEvent("ExitLandingMode");
   Log("RELEASED "+message);target=null;planet=null;if(notify&&message.Length>0)Hint(message);
  }
  void Hint(string text){
   if(Time.unscaledTime<nextHint)return;nextHint=Time.unscaledTime+0.2f;
   if(NotificationManager.SharedInstance!=null)NotificationManager.SharedInstance.PostNotification(new NotificationData(NotificationTarget.Ship,text,5f,true),false);
  }
  void Log(string text){ModHelper.Console.WriteLine("[VR-LANDING] "+text,MessageType.Info);}
  [HarmonyPatch(typeof(ShipCockpitController),"Update")]
  static class InputPatch {
   [HarmonyPrefix,HarmonyPriority(Priority.First)] static void Prefix(){
    if(Instance==null)return;try{Instance.CockpitInput();}catch(Exception ex){Instance.Stop("降落辅助出错，控制已交还。",true);Instance.Log("ERROR INPUT "+ex.Message);}
   }
  }
  [HarmonyPatch(typeof(ShipThrusterController),"ReadTranslationalInput")]
  static class ThrustPatch {
   [HarmonyPostfix,HarmonyPriority(Priority.Last)] static void Postfix(ShipThrusterController __instance,ref Vector3 __result){
    if(Instance==null||!Instance.active||__instance.GetComponent<OWRigidbody>()!=Instance.ship)return;
    try{Instance.Translation(ref __result);}catch(Exception ex){Instance.Stop("降落辅助出错，控制已交还。",true);Instance.Log("ERROR THRUST "+ex.Message);}
   }
  }
 }
}
