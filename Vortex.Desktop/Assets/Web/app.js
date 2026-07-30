(() => {
  'use strict';
  if (window.__VORTEX_COMPACT__) document.body.classList.add('compact');

  const canvas = document.getElementById('orbCanvas');
  const ctx = canvas.getContext('2d', { alpha: true });
  const statePill = document.getElementById('statePill');
  const stateTitle = document.getElementById('stateTitle');
  const stateSubtitle = document.getElementById('stateSubtitle');
  const providerPill = document.getElementById('providerPill');
  const activityText = document.getElementById('activityText');
  const scanLines = document.getElementById('scanLines');
  const toast = document.getElementById('toast');

  let width = 1, height = 1, dpr = 1, time = 0, audio = .04;
  let state = 'idle', recording = false;
  let pointer = { x: 0, y: 0, tx: 0, ty: 0 };
  const particles = Array.from({length: 92}, (_,i) => ({a:Math.random()*Math.PI*2,r:.56+Math.random()*.7,z:Math.random(),s:.002+Math.random()*.005,p:Math.random()*6.28}));

  const labels = {
    idle:['HazÃ„Â±r','Komutunuzu bekliyor'], recording:['Dinliyorum','Ses seviyesi gÃƒÂ¶rsele aktarÃ„Â±lÃ„Â±yor'],
    transcribing:['YazÃ„Â±ya dÃƒÂ¶nÃƒÂ¼Ã…Å¸tÃƒÂ¼rÃƒÂ¼lÃƒÂ¼yor','Ses C# servisi tarafÃ„Â±ndan iÃ…Å¸leniyor'], processing:['Ã„Â°Ã…Å¸leniyor','Vortex gÃƒÂ¶revi deÃ„Å¸erlendiriyor'],
    speaking:['YanÃ„Â±t veriyor','Sesli cevap etkin'], error:['Bir sorun oluÃ…Å¸tu','AyrÃ„Â±ntÃ„Â±lar C# katmanÃ„Â±nda'], offline:['Ãƒâ€¡evrimdÃ„Â±Ã…Å¸Ã„Â±','Provider baÃ„Å¸lantÃ„Â±sÃ„Â± bulunamadÃ„Â±']
  };

  function post(type, value=null){
    const body = JSON.stringify({type,value});
    if (typeof window.invokeCSharpAction === 'function') window.invokeCSharpAction(body);
    else showToast('Ãƒâ€“n izleme modu: ' + type);
  }

  window.vortexHost = { receive(message){
    if (!message || typeof message.type !== 'string') return;
    if (message.type === 'state') setState(String(message.value));
    if (message.type === 'audio-level') audio += (Math.max(0,Math.min(1,Number(message.value)||0))-audio)*.36;
    if (message.type === 'provider-mode' && providerPill) providerPill.textContent=String(message.value).toUpperCase();
    if (message.type === 'host-ready' && activityText) activityText.textContent='C# kÃƒÂ¶prÃƒÂ¼sÃƒÂ¼ hazÃ„Â±r: '+message.value;
    if (message.type === 'host-error' || message.type === 'toast') showToast(String(message.value));
  }};

  function setState(next){
    state = labels[next] ? next : 'idle';
    if (statePill) statePill.textContent = state.toUpperCase();
    if (stateTitle) stateTitle.textContent = labels[state][0];
    if (stateSubtitle) stateSubtitle.textContent = labels[state][1];
    if (activityText) activityText.textContent = labels[state][1];
    recording = state === 'recording';
    if (scanLines) scanLines.className = 'scanLines ' + state;
  }

  function showToast(text){if (toast) {toast.textContent=text;toast.classList.add('show');setTimeout(()=>toast.classList.remove('show'),1800)}}
  function resize(){dpr=Math.min(devicePixelRatio||1,2);width=canvas.clientWidth;height=canvas.clientHeight;canvas.width=width*dpr;canvas.height=height*dpr;ctx.setTransform(dpr,0,0,dpr,0,0)}
  addEventListener('resize',resize);resize();
  addEventListener('pointermove',e=>{pointer.tx=(e.clientX/width-.5);pointer.ty=(e.clientY/height-.5)});

  if (document.body.classList.contains('compact')) {
    document.addEventListener('pointerup', (event) => {
      const interactive = event.target.closest('button, label, input, a');
      if (!interactive) post('restore-main');
    });
  }

  function glowCircle(x,y,r,color,alpha){
    const g=ctx.createRadialGradient(x,y,0,x,y,r);g.addColorStop(0,color.replace('ALPHA',alpha));g.addColorStop(.42,color.replace('ALPHA',alpha*.45));g.addColorStop(1,color.replace('ALPHA','0'));ctx.fillStyle=g;ctx.beginPath();ctx.arc(x,y,r,0,Math.PI*2);ctx.fill();
  }
  function ellipse(cx,cy,rx,ry,rot,alpha,widthLine=1.2){ctx.save();ctx.translate(cx,cy);ctx.rotate(rot);ctx.strokeStyle=`rgba(76,210,255,${alpha})`;ctx.lineWidth=widthLine;ctx.shadowBlur=16;ctx.shadowColor=`rgba(22,181,255,${alpha})`;ctx.beginPath();ctx.ellipse(0,0,rx,ry,0,0,Math.PI*2);ctx.stroke();ctx.restore()}

  function draw(){
    time += .012; pointer.x+=(pointer.tx-pointer.x)*.035;pointer.y+=(pointer.ty-pointer.y)*.035;
    ctx.clearRect(0,0,width,height);
    const compact=document.body.classList.contains('compact');
    const cx=compact?92:width*.52+pointer.x*14, cy=height*.48+pointer.y*10;
    const base=compact?36:Math.min(width,height)*.185;
    const energy=(state==='recording'||state==='speaking')?audio:state==='processing'||state==='transcribing'?.28:.08;
    const pulse=1+Math.sin(time*2.1)*.018+energy*.12;

    glowCircle(cx,cy,base*2.6,'rgba(0,142,255,ALPHA)',.055+energy*.06);
    glowCircle(cx,cy,base*1.75,'rgba(0,224,255,ALPHA)',.085+energy*.09);

    for(let i=0;i<3;i++) ellipse(cx,cy,base*(1.35+i*.25),base*(.44+i*.08),time*(i%2?-.55:.42)+i*.8,.2+i*.08+energy*.28,1+i*.5);

    for(const q of particles){q.a+=q.s*(state==='processing'?2.2:1);const rr=base*q.r*(1+Math.sin(time*1.6+q.p)*.05);const px=cx+Math.cos(q.a+time*.22)*rr*1.65;const py=cy+Math.sin(q.a+time*.22)*rr*.72;const a=.08+q.z*.42+energy*.22;ctx.fillStyle=`rgba(${80+q.z*80},${185+q.z*55},255,${a})`;ctx.beginPath();ctx.arc(px,py,.5+q.z*1.7,0,6.283);ctx.fill()}

    ctx.save();ctx.translate(cx,cy);ctx.scale(pulse,pulse);
    const outer=ctx.createRadialGradient(-base*.28,-base*.35,base*.05,0,0,base*1.12);outer.addColorStop(0,'rgba(157,246,255,.98)');outer.addColorStop(.12,'rgba(27,194,242,.95)');outer.addColorStop(.42,'rgba(6,80,134,.98)');outer.addColorStop(.73,'rgba(2,21,42,.99)');outer.addColorStop(1,'rgba(0,4,12,.98)');ctx.fillStyle=outer;ctx.shadowBlur=44;ctx.shadowColor=`rgba(0,191,255,${.38+energy*.4})`;ctx.beginPath();
    const pts=160;for(let i=0;i<=pts;i++){const a=i/pts*Math.PI*2;const wobble=Math.sin(a*5+time*2.2)*energy*.045+Math.sin(a*9-time)*.012;const r=base*(1+wobble);const x=Math.cos(a)*r,y=Math.sin(a)*r;if(i===0)ctx.moveTo(x,y);else ctx.lineTo(x,y)}ctx.closePath();ctx.fill();
    ctx.globalCompositeOperation='screen';for(let i=0;i<22;i++){const a=i/22*6.283+time*.15;ctx.strokeStyle=`rgba(52,204,255,${.025+(i%5)*.012})`;ctx.lineWidth=.7;ctx.beginPath();ctx.arc(Math.sin(a*2)*base*.18,Math.cos(a*3)*base*.12,base*(.45+i*.018),a,a+1.4);ctx.stroke()}ctx.restore();

    const bars=compact?7:15;ctx.save();ctx.translate(cx,cy);for(let i=0;i<bars;i++){const n=i-(bars-1)/2;const wave=state==='recording'||state==='speaking'?(8+energy*base*.42*(.35+Math.abs(Math.sin(time*7+i*.7)))):state==='processing'||state==='transcribing'?(10+Math.sin(time*4+i*.5)*3):22;ctx.strokeStyle=`rgba(184,248,255,${.45+energy*.45})`;ctx.lineWidth=compact?1.2:1.7;ctx.beginPath();ctx.moveTo(n*(compact?3.2:4.2),-wave/2);ctx.lineTo(n*(compact?3.2:4.2),wave/2);ctx.stroke()}ctx.restore();
    requestAnimationFrame(draw);
  }
  setState('idle');draw();
})();
