await input([{kind:'click',button:1,ms:100}]);
checkpoint('initial', await capture({label:'final-native-initial'}));
await keyboard.hold(['R'],100);
await keyboard.hold(['Q'],10000);
await keyboard.hold(['Q'],10000);
checkpoint('suspended', await capture({label:'final-native-reeled'}));
await keyboard.hold(['A'],300);
for(let i=0;i<6;i++){await sleep(1000);checkpoint('arc-'+i,await capture({label:'final-native-arc-'+i}));}
await keyboard.hold(['D'],200);
await keyboard.hold(['T'],100);
checkpoint('release',await capture({label:'final-native-release'}));
await sleep(1500);
checkpoint('landing',await capture({label:'final-native-landing'}));
