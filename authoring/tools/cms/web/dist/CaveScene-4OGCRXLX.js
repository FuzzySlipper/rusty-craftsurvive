import{$ as Re,Aa as q,B as $,C as le,Ca as We,D as Te,E as j,Ea as K,Fa as X,G as de,Ga as Ve,H as ee,Ha as Ge,I as ze,K as M,L as fe,N as Ue,O as k,T as ue,V as pe,_ as Me,aa as Le,b as be,ba as me,ca as De,fa as He,ga as Be,pa as Ce,qa as Oe,s as _e,t as Ae,ua as Pe,v as w,wa as Ie,y as P,z as Z}from"./chunk-5Y4YGKBX.js";var Fe=new j,te=new w,I=class extends Ie{constructor(){super(),this.isLineSegmentsGeometry=!0,this.type="LineSegmentsGeometry";let e=[-1,2,0,1,2,0,-1,1,0,1,1,0,-1,0,0,1,0,0,-1,-1,0,1,-1,0],n=[-1,2,1,2,-1,1,1,1,-1,-1,1,-1,-1,-2,1,-2],i=[0,2,1,2,3,1,2,4,3,4,5,3,4,6,5,6,7,5];this.setIndex(i),this.setAttribute("position",new de(e,3)),this.setAttribute("uv",new de(n,2))}applyMatrix4(e){let n=this.attributes.instanceStart,i=this.attributes.instanceEnd;return n!==void 0&&(n.applyMatrix4(e),i.applyMatrix4(e),n.needsUpdate=!0),this.boundingBox!==null&&this.computeBoundingBox(),this.boundingSphere!==null&&this.computeBoundingSphere(),this}setPositions(e){let n;e instanceof Float32Array?n=e:Array.isArray(e)&&(n=new Float32Array(e));let i=new q(n,6,1);return this.setAttribute("instanceStart",new M(i,3,0)),this.setAttribute("instanceEnd",new M(i,3,3)),this.instanceCount=this.attributes.instanceStart.count,this.computeBoundingBox(),this.computeBoundingSphere(),this}setColors(e){let n;e instanceof Float32Array?n=e:Array.isArray(e)&&(n=new Float32Array(e));let i=new q(n,6,1);return this.setAttribute("instanceColorStart",new M(i,3,0)),this.setAttribute("instanceColorEnd",new M(i,3,3)),this}fromWireframeGeometry(e){return this.setPositions(e.attributes.position.array),this}fromEdgesGeometry(e){return this.setPositions(e.attributes.position.array),this}fromMesh(e){return this.fromWireframeGeometry(new Le(e.geometry)),this}fromLineSegments(e){let n=e.geometry;return this.setPositions(n.attributes.position.array),this}computeBoundingBox(){this.boundingBox===null&&(this.boundingBox=new j);let e=this.attributes.instanceStart,n=this.attributes.instanceEnd;e!==void 0&&n!==void 0&&(this.boundingBox.setFromBufferAttribute(e),Fe.setFromBufferAttribute(n),this.boundingBox.union(Fe))}computeBoundingSphere(){this.boundingSphere===null&&(this.boundingSphere=new ee),this.boundingBox===null&&this.computeBoundingBox();let e=this.attributes.instanceStart,n=this.attributes.instanceEnd;if(e!==void 0&&n!==void 0){let i=this.boundingSphere.center;this.boundingBox.getCenter(i);let s=0;for(let a=0,p=e.count;a<p;a++)te.fromBufferAttribute(e,a),s=Math.max(s,i.distanceToSquared(te)),te.fromBufferAttribute(n,a),s=Math.max(s,i.distanceToSquared(te));this.boundingSphere.radius=Math.sqrt(s),isNaN(this.boundingSphere.radius)&&console.error("THREE.LineSegmentsGeometry.computeBoundingSphere(): Computed radius is NaN. The instanced position data is likely to have NaN values.",this)}}toJSON(){}};K.line={worldUnits:{value:1},linewidth:{value:1},resolution:{value:new Ae},dashOffset:{value:0},dashScale:{value:1},dashSize:{value:1},gapSize:{value:1}};X.line={uniforms:me.merge([K.common,K.fog,K.line]),vertexShader:`
		#include <common>
		#include <color_pars_vertex>
		#include <fog_pars_vertex>
		#include <logdepthbuf_pars_vertex>
		#include <clipping_planes_pars_vertex>

		uniform float linewidth;
		uniform vec2 resolution;

		attribute vec3 instanceStart;
		attribute vec3 instanceEnd;

		attribute vec3 instanceColorStart;
		attribute vec3 instanceColorEnd;

		#ifdef WORLD_UNITS

			varying vec4 worldPos;
			varying vec3 worldStart;
			varying vec3 worldEnd;

			#ifdef USE_DASH

				varying vec2 vUv;

			#endif

		#else

			varying vec2 vUv;

		#endif

		#ifdef USE_DASH

			uniform float dashScale;
			attribute float instanceDistanceStart;
			attribute float instanceDistanceEnd;
			varying float vLineDistance;

		#endif

		float trimSegmentAlpha( const in vec4 start, const in vec4 end ) {

			// compute the interpolation factor needed to trim the segment so it terminates
			// between the camera plane and the near plane

			// conservative estimate of the near plane
			float a = projectionMatrix[ 2 ][ 2 ]; // 3nd entry in 3th column
			float b = projectionMatrix[ 3 ][ 2 ]; // 3nd entry in 4th column

			// we need different nearEstimate formula for reversed and default depth buffer
			// a is positive with a reversed depth buffer so it can be used for controlling the code flow
			float nearEstimate = ( a > 0.0 ) ? ( - b / ( a + 1.0 ) ) : ( - 0.5 * b / a );

			return ( nearEstimate - start.z ) / ( end.z - start.z );

		}

		void main() {

			#ifdef USE_COLOR

				vColor.xyz = ( position.y < 0.5 ) ? instanceColorStart : instanceColorEnd;

			#endif

			float aspect = resolution.x / resolution.y;

			// camera space
			vec4 start = modelViewMatrix * vec4( instanceStart, 1.0 );
			vec4 end = modelViewMatrix * vec4( instanceEnd, 1.0 );

			#ifdef USE_DASH

				float lineDistanceStart = dashScale * instanceDistanceStart;
				float lineDistanceEnd = dashScale * instanceDistanceEnd;

			#endif

			#ifdef WORLD_UNITS

				worldStart = start.xyz;
				worldEnd = end.xyz;

			#else

				vUv = uv;

			#endif

			// special case for perspective projection, and segments that terminate either in, or behind, the camera plane
			// clearly the gpu firmware has a way of addressing this issue when projecting into ndc space
			// but we need to perform ndc-space calculations in the shader, so we must address this issue directly
			// perhaps there is a more elegant solution -- WestLangley

			bool perspective = ( projectionMatrix[ 2 ][ 3 ] == - 1.0 ); // 4th entry in the 3rd column

			if ( perspective ) {

				if ( start.z < 0.0 && end.z >= 0.0 ) {

					float alpha = trimSegmentAlpha( start, end );
					end.xyz = mix( start.xyz, end.xyz, alpha );

					#ifdef USE_DASH

						lineDistanceEnd = mix( lineDistanceStart, lineDistanceEnd, alpha );

					#endif

				} else if ( end.z < 0.0 && start.z >= 0.0 ) {

					float alpha = trimSegmentAlpha( end, start );
					start.xyz = mix( end.xyz, start.xyz, alpha );

					#ifdef USE_DASH

						lineDistanceStart = mix( lineDistanceEnd, lineDistanceStart, alpha );

					#endif

				}

			}

			#ifdef USE_DASH

				vLineDistance = ( position.y < 0.5 ) ? lineDistanceStart : lineDistanceEnd;
				vUv = uv;

			#endif

			// clip space
			vec4 clipStart = projectionMatrix * start;
			vec4 clipEnd = projectionMatrix * end;

			// ndc space
			vec3 ndcStart = clipStart.xyz / clipStart.w;
			vec3 ndcEnd = clipEnd.xyz / clipEnd.w;

			// direction
			vec2 dir = ndcEnd.xy - ndcStart.xy;

			// account for clip-space aspect ratio
			dir.x *= aspect;
			dir = normalize( dir );

			#ifdef WORLD_UNITS

				vec3 worldDir = normalize( end.xyz - start.xyz );
				vec3 tmpFwd = perspective ? normalize( mix( start.xyz, end.xyz, 0.5 ) ) : vec3( 0.0, 0.0, - 1.0 );
				vec3 worldUp = normalize( cross( worldDir, tmpFwd ) );
				vec3 worldFwd = cross( worldDir, worldUp );
				worldPos = position.y < 0.5 ? start: end;

				// height offset
				float hw = linewidth * 0.5;
				worldPos.xyz += position.x < 0.0 ? hw * worldUp : - hw * worldUp;

				// don't extend the line if we're rendering dashes because we
				// won't be rendering the endcaps
				#ifndef USE_DASH

					// cap extension
					worldPos.xyz += position.y < 0.5 ? - hw * worldDir : hw * worldDir;

					// add width to the box
					worldPos.xyz += worldFwd * hw;

					// endcaps
					if ( position.y > 1.0 || position.y < 0.0 ) {

						worldPos.xyz -= worldFwd * 2.0 * hw;

					}

				#endif

				// project the worldpos
				vec4 clip = projectionMatrix * worldPos;

				// shift the depth of the projected points so the line
				// segments overlap neatly
				vec3 clipPose = ( position.y < 0.5 ) ? ndcStart : ndcEnd;

				// only shift the depth of vertices in front of the camera (#35137)
				if ( clip.w > 0.0 ) clip.z = clipPose.z * clip.w;

			#else

				vec2 offset = vec2( dir.y, - dir.x );
				// undo aspect ratio adjustment
				dir.x /= aspect;
				offset.x /= aspect;

				// sign flip
				if ( position.x < 0.0 ) offset *= - 1.0;

				// endcaps
				if ( position.y < 0.0 ) {

					offset += - dir;

				} else if ( position.y > 1.0 ) {

					offset += dir;

				}

				// adjust for linewidth
				offset *= linewidth;

				// adjust for clip-space to screen-space conversion // maybe resolution should be based on viewport ...
				offset /= resolution.y;

				// select end
				vec4 clip = ( position.y < 0.5 ) ? clipStart : clipEnd;

				// back to clip space
				offset *= clip.w;

				clip.xy += offset;

			#endif

			gl_Position = clip;

			vec4 mvPosition = ( position.y < 0.5 ) ? start : end; // this is an approximation

			#include <logdepthbuf_vertex>
			#include <clipping_planes_vertex>
			#include <fog_vertex>

		}
		`,fragmentShader:`
		uniform vec3 diffuse;
		uniform float opacity;
		uniform float linewidth;

		#ifdef USE_DASH

			uniform float dashOffset;
			uniform float dashSize;
			uniform float gapSize;

		#endif

		varying float vLineDistance;

		#ifdef WORLD_UNITS

			varying vec4 worldPos;
			varying vec3 worldStart;
			varying vec3 worldEnd;

			#ifdef USE_DASH

				varying vec2 vUv;

			#endif

		#else

			varying vec2 vUv;

		#endif

		#include <common>
		#include <color_pars_fragment>
		#include <fog_pars_fragment>
		#include <logdepthbuf_pars_fragment>
		#include <clipping_planes_pars_fragment>

		vec2 closestLineToLine(vec3 p1, vec3 p2, vec3 p3, vec3 p4) {

			float mua;
			float mub;

			vec3 p13 = p1 - p3;
			vec3 p43 = p4 - p3;

			vec3 p21 = p2 - p1;

			float d1343 = dot( p13, p43 );
			float d4321 = dot( p43, p21 );
			float d1321 = dot( p13, p21 );
			float d4343 = dot( p43, p43 );
			float d2121 = dot( p21, p21 );

			float denom = d2121 * d4343 - d4321 * d4321;

			float numer = d1343 * d4321 - d1321 * d4343;

			mua = numer / denom;
			mua = clamp( mua, 0.0, 1.0 );
			mub = ( d1343 + d4321 * ( mua ) ) / d4343;
			mub = clamp( mub, 0.0, 1.0 );

			return vec2( mua, mub );

		}

		void main() {

			float alpha = opacity;
			vec4 diffuseColor = vec4( diffuse, alpha );

			#include <clipping_planes_fragment>

			#ifdef USE_DASH

				if ( vUv.y < - 1.0 || vUv.y > 1.0 ) discard; // discard endcaps

				if ( mod( vLineDistance + dashOffset, dashSize + gapSize ) > dashSize ) discard; // todo - FIX

			#endif

			#ifdef WORLD_UNITS

				float len;

				if ( isOrthographic ) {

					// View rays are parallel to the z axis so the distance reduces to camera-space XY
					vec2 lineDir = worldEnd.xy - worldStart.xy;
					float t = clamp( dot( worldPos.xy - worldStart.xy, lineDir ) / dot( lineDir, lineDir ), 0.0, 1.0 );
					len = length( worldStart.xy + lineDir * t - worldPos.xy );

				} else {

					// Find the closest points on the view ray and the line segment
					vec3 rayEnd = normalize( worldPos.xyz ) * 1e5;
					vec3 lineDir = worldEnd - worldStart;
					vec2 params = closestLineToLine( worldStart, worldEnd, vec3( 0.0, 0.0, 0.0 ), rayEnd );

					vec3 p1 = worldStart + lineDir * params.x;
					vec3 p2 = rayEnd * params.y;
					vec3 delta = p1 - p2;
					len = length( delta );

				}

				float norm = len / linewidth;

				#ifndef USE_DASH

					#ifdef USE_ALPHA_TO_COVERAGE

						float dnorm = fwidth( norm );
						alpha = 1.0 - smoothstep( 0.5 - dnorm, 0.5 + dnorm, norm );

					#else

						if ( norm > 0.5 ) {

							discard;

						}

					#endif

				#endif

			#else

				#ifdef USE_ALPHA_TO_COVERAGE

					// artifacts appear on some hardware if a derivative is taken within a conditional
					float a = vUv.x;
					float b = ( vUv.y > 0.0 ) ? vUv.y - 1.0 : vUv.y + 1.0;
					float len2 = a * a + b * b;
					float dlen = fwidth( len2 );

					if ( abs( vUv.y ) > 1.0 ) {

						alpha = 1.0 - smoothstep( 1.0 - dlen, 1.0 + dlen, len2 );

					}

				#else

					if ( abs( vUv.y ) > 1.0 ) {

						float a = vUv.x;
						float b = ( vUv.y > 0.0 ) ? vUv.y - 1.0 : vUv.y + 1.0;
						float len2 = a * a + b * b;

						if ( len2 > 1.0 ) discard;

					}

				#endif

			#endif

			#include <logdepthbuf_fragment>
			#include <color_fragment>

			gl_FragColor = vec4( diffuseColor.rgb, alpha );

			#include <tonemapping_fragment>
			#include <colorspace_fragment>
			#include <fog_fragment>
			#include <premultiplied_alpha_fragment>

		}
		`};var W=class extends De{constructor(e){super({type:"LineMaterial",uniforms:me.clone(X.line.uniforms),vertexShader:X.line.vertexShader,fragmentShader:X.line.fragmentShader,clipping:!0}),this.isLineMaterial=!0,this.setValues(e)}get color(){return this.uniforms.diffuse.value}set color(e){this.uniforms.diffuse.value=e}get worldUnits(){return"WORLD_UNITS"in this.defines}set worldUnits(e){e===!0!==this.worldUnits&&(this.needsUpdate=!0),e===!0?this.defines.WORLD_UNITS="":delete this.defines.WORLD_UNITS}get linewidth(){return this.uniforms.linewidth.value}set linewidth(e){this.uniforms.linewidth&&(this.uniforms.linewidth.value=e)}get dashed(){return"USE_DASH"in this.defines}set dashed(e){e===!0!==this.dashed&&(this.needsUpdate=!0),e===!0?this.defines.USE_DASH="":delete this.defines.USE_DASH}get dashScale(){return this.uniforms.dashScale.value}set dashScale(e){this.uniforms.dashScale.value=e}get dashSize(){return this.uniforms.dashSize.value}set dashSize(e){this.uniforms.dashSize.value=e}get dashOffset(){return this.uniforms.dashOffset.value}set dashOffset(e){this.uniforms.dashOffset.value=e}get gapSize(){return this.uniforms.gapSize.value}set gapSize(e){this.uniforms.gapSize.value=e}get opacity(){return this.uniforms.opacity.value}set opacity(e){this.uniforms&&(this.uniforms.opacity.value=e)}get resolution(){return this.uniforms.resolution.value}set resolution(e){this.uniforms.resolution.value.copy(e)}get alphaToCoverage(){return"USE_ALPHA_TO_COVERAGE"in this.defines}set alphaToCoverage(e){this.defines&&(e===!0!==this.alphaToCoverage&&(this.needsUpdate=!0),e===!0?this.defines.USE_ALPHA_TO_COVERAGE="":delete this.defines.USE_ALPHA_TO_COVERAGE)}};var he=new P,Ne=new w,je=new w,y=new P,x=new P,A=new P,ve=new w,we=new Z,g=new We,ke=new w,ne=new j,ie=new ee,T=new P,z,B;function qe(l,e,n){return T.set(0,0,-e,1).applyMatrix4(l.projectionMatrix),T.multiplyScalar(1/T.w),T.x=B/n.width,T.y=B/n.height,T.applyMatrix4(l.projectionMatrixInverse),T.multiplyScalar(1/T.w),Math.abs(Math.max(T.x,T.y))}function Ye(l,e){let n=l.matrixWorld,i=l.geometry,s=i.attributes.instanceStart,a=i.attributes.instanceEnd,p=Math.min(i.instanceCount,s.count);for(let o=0,m=p;o<m;o++){g.start.fromBufferAttribute(s,o),g.end.fromBufferAttribute(a,o),g.applyMatrix4(n);let S=new w,c=new w;z.distanceSqToSegment(g.start,g.end,c,S),c.distanceTo(S)<B*.5&&e.push({point:c,pointOnLine:S,distance:z.origin.distanceTo(c),object:l,face:null,faceIndex:o,uv:null,uv1:null})}}function Je(l,e,n){let i=e.projectionMatrix,a=l.material.resolution,p=l.matrixWorld,o=l.geometry,m=o.attributes.instanceStart,S=o.attributes.instanceEnd,c=Math.min(o.instanceCount,m.count),E=-e.near;z.at(1,A),A.w=1,A.applyMatrix4(e.matrixWorldInverse),A.applyMatrix4(i),A.multiplyScalar(1/A.w),A.x*=a.x/2,A.y*=a.y/2,A.z=0,ve.copy(A),we.multiplyMatrices(e.matrixWorldInverse,p);for(let h=0,Y=c;h<Y;h++){if(y.fromBufferAttribute(m,h),x.fromBufferAttribute(S,h),y.w=1,x.w=1,y.applyMatrix4(we),x.applyMatrix4(we),y.z>E&&x.z>E)continue;if(y.z>E){let U=y.z-x.z,b=(y.z-E)/U;y.lerp(x,b)}else if(x.z>E){let U=x.z-y.z,b=(x.z-E)/U;x.lerp(y,b)}y.applyMatrix4(i),x.applyMatrix4(i),y.multiplyScalar(1/y.w),x.multiplyScalar(1/x.w),y.x*=a.x/2,y.y*=a.y/2,x.x*=a.x/2,x.y*=a.y/2,g.start.copy(y),g.start.z=0,g.end.copy(x),g.end.z=0;let R=g.closestPointToPointParameter(ve,!0);g.at(R,ke);let V=_e.lerp(y.z,x.z,R),oe=V>=-1&&V<=1,J=ve.distanceTo(ke)<B*.5;if(oe&&J){g.start.fromBufferAttribute(m,h),g.end.fromBufferAttribute(S,h),g.start.applyMatrix4(p),g.end.applyMatrix4(p);let U=new w,b=new w;z.distanceSqToSegment(g.start,g.end,b,U),n.push({point:b,pointOnLine:U,distance:z.origin.distanceTo(b),object:l,face:null,faceIndex:h,uv:null,uv1:null})}}}var re=class extends k{constructor(e=new I,n=new W({color:Math.random()*16777215})){super(e,n),this.isLineSegments2=!0,this.type="LineSegments2"}computeLineDistances(){let e=this.geometry,n=e.attributes.instanceStart,i=e.attributes.instanceEnd,s=new Float32Array(2*n.count);for(let p=0,o=0,m=n.count;p<m;p++,o+=2)Ne.fromBufferAttribute(n,p),je.fromBufferAttribute(i,p),s[o]=o===0?0:s[o-1],s[o+1]=s[o]+Ne.distanceTo(je);let a=new q(s,2,1);return e.setAttribute("instanceDistanceStart",new M(a,1,0)),e.setAttribute("instanceDistanceEnd",new M(a,1,1)),this}raycast(e,n){let i=this.material.worldUnits,s=e.camera;if(s===null&&!i&&console.error('LineSegments2: "Raycaster.camera" needs to be set in order to raycast against LineSegments2 while worldUnits is set to false.'),i===!1&&(this.material.resolution.x===0||this.material.resolution.y===0))return;let a=e.params.Line2!==void 0&&e.params.Line2.threshold||0;z=e.ray;let p=this.matrixWorld,o=this.geometry,m=this.material;B=m.linewidth+a,o.boundingSphere===null&&o.computeBoundingSphere(),ie.copy(o.boundingSphere).applyMatrix4(p);let S;if(i)S=B*.5;else{let E=Math.max(s.near,ie.distanceToPoint(z.origin));S=qe(s,E,m.resolution)}if(ie.radius+=S,z.intersectsSphere(ie)===!1)return;o.boundingBox===null&&o.computeBoundingBox(),ne.copy(o.boundingBox).applyMatrix4(p);let c;if(i)c=B*.5;else{let E=Math.max(s.near,ne.distanceToPoint(z.origin));c=qe(s,E,m.resolution)}ne.expandByScalar(c),z.intersectsBox(ne)!==!1&&(i?Ye(this,n):Je(this,s,n))}onBeforeRender(e){let n=this.material.uniforms;n&&n.resolution&&(e.getViewport(he),this.material.uniforms.resolution.value.set(he.z,he.w))}};var Qe=[1,2,3,4,5],Ze=1053714,$e=45,et=1.75,ye={depthTest:!1,transparent:!0},tt=.9,nt=3,it={1:12863552,2:11033804,3:14055510,4:14592076,5:6339192},rt=[12863552,14055510,14592076,6339192],C={chamber:16765286,sink:7521535,spring:5103817,water:3903446,ruin:12094704,start:16777215,deadEnd:10129277};function st(l){return Uint8Array.from(atob(l),e=>e.charCodeAt(0))}function gt(l,e){let n=l.clientWidth||800,i=l.clientHeight||520,s=new Ve({antialias:!0});s.setPixelRatio(window.devicePixelRatio),s.setSize(n,i),s.localClippingEnabled=!0,l.replaceChildren(s.domElement);let a=new Te;a.background=new le(Ze),a.add(new Ce(15920869,2831148,1.5));let p=new Pe(16777215,1.6);p.position.set(-40,90,60),a.add(p);let[o,m,S]=e.cells.size,c=e.cells.metres,E=st(e.cells.state),h=(t,r,v)=>t<0||r<0||v<0||t>=o||r>=m||v>=S?0:E[(v*m+r)*o+t],Y=new fe(new w(0,-1,0),m*c),se=new fe(new w(-1,0,0),o*c),R=[Y,se],V=(t,r,v)=>{let _=0;for(let d=-1;d<=1;d++)for(let f=-1;f<=1;f++)for(let u=-1;u<=1;u++)_=Math.max(_,h(t+u,r+f,v+d));return _},oe=new Me(c,c,c),J=new Map,U=new Z;for(let t of Qe){let r=[];for(let d=0;d<S;d++)for(let f=0;f<m;f++)for(let u=0;u<o;u++){if(!h(u,f,d)||h(u+1,f,d)&&h(u-1,f,d)&&h(u,f+1,d)&&h(u,f-1,d)&&h(u,f,d+1)&&h(u,f,d-1))continue;let N=h(u,f,d);(N===2?2:Math.max(N,V(u,f,d)===2?N:V(u,f,d)))===t&&r.push([u,f,d])}let v=new He({color:it[t],clippingPlanes:R,side:be}),_=new ue(oe,v,Math.max(1,r.length));_.count=r.length,r.forEach(([d,f,u],N)=>{U.makeTranslation((d+.5)*c,(f+.5)*c,(u+.5)*c),_.setMatrixAt(N,U)}),J.set(t,_),a.add(_)}let b={route:new $,tendril:new $,markers:new $};for(let t of Object.values(b))t.renderOrder=1,a.add(t);let xe=.3,ae=[],ge=[],L=new le;for(let t=1;t<e.route.length;t++){let r=e.route[t-1],v=e.route[t];ae.push(r[0],r[1]+xe,r[2],v[0],v[1]+xe,v[2]),L.setHex(rt[e.profile[t][2]]),ge.push(L.r,L.g,L.b,L.r,L.g,L.b)}let ce=new W({linewidth:nt,vertexColors:!0,clippingPlanes:R,...ye});if(ce.resolution.set(n,i),ae.length){let t=new I().setPositions(ae).setColors(ge);b.route.add(new re(t,ce))}if(e.tendril.length>1){let t=new ze().setFromPoints(e.tendril.map(r=>new w(r[0],r[1],r[2])));b.tendril.add(new pe(t,new Be({color:16747100,dashSize:1,gapSize:.6,clippingPlanes:R,...ye})).computeLineDistances())}let Ke=new Re(tt,12,8),D=(t,r,v=1)=>{if(!t)return;let _=new k(Ke,new Ue({color:r,clippingPlanes:R,...ye}));_.position.set(t[0],t[1],t[2]),_.scale.setScalar(v),b.markers.add(_)},O=e.features;for(let t of O.chambers??[])D(t,C.chamber,1.3);for(let t of O.sinks??[])D(t,C.sink);D(O.spring,C.spring,1.2);for(let t of O.water??[])D(t.at,C.water);D(O.ruin,C.ruin,1.2);for(let t of O.deadEnds??[])D(t,C.deadEnd,.7);D(e.start,C.start,1.1);let G=new Oe($e,n/i,.1,2e3),Se=new w(o*c/2,m*c/2,S*c/2),Xe=Math.max(o,m,S)*c*et;G.position.copy(Se).add(new w(.75,.8,1).normalize().multiplyScalar(Xe));let F=new Ge(G,s.domElement);F.target.copy(Se),F.update();let Q=0,H=()=>{Q||(Q=requestAnimationFrame(()=>{Q=0,s.render(a,G)}))};F.addEventListener("change",H),H();let Ee=new ResizeObserver(()=>{let t=l.clientWidth,r=l.clientHeight;!t||!r||(s.setSize(t,r),ce.resolution.set(t,r),G.aspect=t/r,G.updateProjectionMatrix(),H())});return Ee.observe(l),{setStates(t){for(let[r,v]of J)v.visible=t[r]!==!1;H()},setLayers(t){for(let[r,v]of Object.entries(b))v.visible=t[r]!==!1;H()},setCut(t,r){Y.constant=t,se.constant=r??o*c+1,H()},dispose(){cancelAnimationFrame(Q),F.removeEventListener("change",H),Ee.disconnect(),F.dispose(),a.traverse(t=>{(t instanceof k||t instanceof pe||t instanceof ue)&&(t.geometry.dispose(),(Array.isArray(t.material)?t.material:[t.material]).forEach(r=>r.dispose()))}),s.dispose()}}}export{Qe as CAVE_STATES,gt as mountCave};
