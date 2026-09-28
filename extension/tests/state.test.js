import test from 'node:test';
import assert from 'node:assert/strict';
import {normalizeDomain, summarize} from '../state.js';
test('only domain is retained; private URL fields are discarded',()=>{
  assert.equal(normalizeDomain('https://www.youtube.com/watch?v=secret&q=private'),'youtube.com');
  assert.equal(normalizeDomain('file:///C:/passwords.txt'),'');
  assert.equal(normalizeDomain('chrome://settings'),'');
});
test('YouTube in a background tab does not become foreground time',()=>{
  const tabs=[{id:1,windowId:3,url:'https://www.youtube.com/watch?v=123'},{id:2,windowId:3,url:'https://school.example/task'}];
  const result=summarize(tabs,3,2,new Map([[1,{playing:true,received:1000}]]),2000);
  assert.equal(result.domain,'school.example');assert.equal(result.foregroundPlaying,false);assert.equal(result.backgroundPlaying,true);
});
test('minimized browser only contributes separate background playback',()=>{
  const result=summarize([{id:1,windowId:3,url:'https://www.youtube.com/'}],undefined,undefined,new Map([[1,{playing:true,received:1000}]]),2000);
  assert.equal(result.focused,false);assert.equal(result.domain,'');assert.equal(result.backgroundPlaying,true);
});
test('stale media and incognito are excluded',()=>{
  assert.equal(summarize([{id:1,windowId:3,url:'https://www.youtube.com/'}],3,1,new Map([[1,{playing:true,received:0}]]),10000).foregroundPlaying,false);
  assert.equal(summarize([{id:1,windowId:3,incognito:true,url:'https://private.example'}],3,1,new Map(),0).domain,'');
});
