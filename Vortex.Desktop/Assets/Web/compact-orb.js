(() => {
  'use strict';
  const canvas=document.getElementById('orb');
  const ctx=canvas.getContext('2d',{alpha:true});
  let w=1,h=1,dpr=1,t=0,level=.04,state='idle';
  const particles=Array.from({length:34},()=>({a:Math.random()*Math.PI*2,r:.58+Math.random()*.72,z:Math.random(),s:.003+Math.random()*.007,p:Math.random()*6.28}));
  window.vortexCompactHost={receive(m){if(!m)return;if(m.type==='state')state=String(m.value||'idle');if(m.type==='audio-level')level+=(Math.max(0,Math.min(1,Number(m.value)||0))-level)*.42;}};
  function resize(){dpr=Math.min(devicePixelRatio||1,2);w=canvas.clientWidth;h=canvas.clientHeight;canvas.width=Math.max(1,w*dpr);canvas.height=Math.max(1,h*dpr);ctx.setTransform(dpr,0,0,dpr,0,0)}
  addEventListener('resize',resize);resize();
  function glow(x,y,r,a){const g=ctx.createRadialGradient(x,y,0,x,y,r);g.addColorStop(0,`rgba(0,225,255,${a})`);g.addColorStop(.45,`rgba(0,123,255,${a*.46})`);g.addColorStop(1,'rgba(0,0,0,0)');ctx.fillStyle=g;ctx.beginPath();ctx.arc(x,y,r,0,6.283);ctx.fill()}
  function ring(cx,cy,rx,ry,rot,a,lw){ctx.save();ctx.translate(cx,cy);ctx.rotate(rot);ctx.strokeStyle=`rgba(77,218,255,${a})`;ctx.lineWidth=lw;ctx.shadowBlur=10;ctx.shadowColor='rgba(0,190,255,.8)';ctx.beginPath();ctx.ellipse(0,0,rx,ry,0,0,6.283);ctx.stroke();ctx.restore()}
  function draw(){t+=.016;ctx.clearRect(0,0,w,h);const cx=w/2,cy=h/2,base=Math.min(w,h)*.27;const active=state==='recording'||state==='speaking';const energy=active?level:(state==='processing'||state==='transcribing'?0.28:0.07);const pulse=1+Math.sin(t*2.2)*.02+energy*.13;
    glow(cx,cy,base*2.2,.12+energy*.18);ring(cx,cy,base*1.42,base*.46,t*.52,.33+energy*.25,1);ring(cx,cy,base*1.68,base*.58,-t*.39+.8,.22+energy*.22,1.2);
    for(const p of particles){p.a+=p.s*(state==='processing'?2:1);const rr=base*p.r;const x=cx+Math.cos(p.a+t*.18)*rr*1.55,y=cy+Math.sin(p.a+t*.18)*rr*.72;ctx.fillStyle=`rgba(${90+p.z*90},${188+p.z*55},255,${.12+p.z*.5})`;ctx.beginPath();ctx.arc(x,y,.5+p.z*1.25,0,6.283);ctx.fill()}
    ctx.save();ctx.translate(cx,cy);ctx.scale(pulse,pulse);const g=ctx.createRadialGradient(-base*.28,-base*.34,1,0,0,base*1.08);g.addColorStop(0,'rgba(190,252,255,.98)');g.addColorStop(.14,'rgba(36,212,245,.96)');g.addColorStop(.43,'rgba(4,92,150,.98)');g.addColorStop(.76,'rgba(2,23,45,.99)');g.addColorStop(1,'rgba(0,5,13,1)');ctx.fillStyle=g;ctx.shadowBlur=28;ctx.shadowColor=`rgba(0,195,255,${.42+energy*.38})`;ctx.beginPath();for(let i=0;i<=120;i++){const a=i/120*6.283;const wobble=Math.sin(a*5+t*2.3)*energy*.05+Math.sin(a*9-t)*.012;const r=base*(1+wobble);const x=Math.cos(a)*r,y=Math.sin(a)*r;i?ctx.lineTo(x,y):ctx.moveTo(x,y)}ctx.closePath();ctx.fill();ctx.globalCompositeOperation='screen';for(let i=0;i<12;i++){const a=i/12*6.283+t*.14;ctx.strokeStyle=`rgba(60,212,255,${.035+(i%4)*.018})`;ctx.lineWidth=.6;ctx.beginPath();ctx.arc(Math.sin(a*2)*base*.14,Math.cos(a*3)*base*.1,base*(.47+i*.025),a,a+1.2);ctx.stroke()}ctx.restore();
    const bars=7;ctx.save();ctx.translate(cx,cy);for(let i=0;i<bars;i++){const n=i-3;const hh=active?6+energy*base*.45*(.35+Math.abs(Math.sin(t*7+i*.7))):state==='processing'||state==='transcribing'?7+Math.sin(t*4+i*.5)*2.5:13;ctx.strokeStyle=`rgba(210,251,255,${.5+energy*.42})`;ctx.lineWidth=1;ctx.beginPath();ctx.moveTo(n*3,-hh/2);ctx.lineTo(n*3,hh/2);ctx.stroke()}ctx.restore();requestAnimationFrame(draw)}draw();
})();
