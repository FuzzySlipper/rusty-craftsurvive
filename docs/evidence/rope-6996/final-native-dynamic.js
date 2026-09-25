await keyboard.hold(['R'],100);
await keyboard.hold(['Q'],6000);
checkpoint('loaded-dynamic',await capture({label:'dynamic-loaded'}));
await keyboard.hold(['A'],400);
await sleep(1000);
checkpoint('reaction',await capture({label:'dynamic-reaction'}));
await keyboard.hold(['Z'],2000);
checkpoint('lengthening',await capture({label:'dynamic-lengthening'}));
for(let i=0;i<3;i++){await keyboard.hold(['T'],100);await sleep(200);await keyboard.hold(['R'],100);await sleep(750);checkpoint('reattach-'+i,await capture({label:'dynamic-repeat-'+i}));}
await keyboard.hold(['T'],100);
