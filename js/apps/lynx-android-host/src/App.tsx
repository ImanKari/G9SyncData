import { useEffect, useState } from '@lynx-js/react';
import { runDeviceTest, type DeviceTestConfig } from './deviceTest';

interface GlobalProps {
  g9Server?: string;
  g9Protocol?: string;
  g9RunId?: string;
  g9Files?: string;
  g9Mode?: string;
  g9Hub?: string;
  g9LynxVersion?: string;
  g9PrimjsVersion?: string;
  g9Device?: string;
}

function readConfig(): DeviceTestConfig {
  const props = ((lynx as unknown as { __globalProps?: GlobalProps }).__globalProps ?? {}) as GlobalProps;
  return {
    server: props.g9Server ?? 'http://10.0.2.2:5210',
    protocol: props.g9Protocol === 'json' ? 'json' : 'messagepack',
    runId: props.g9RunId ?? String(Date.now()),
    files: props.g9Files === 'on',
    mode: props.g9Mode === 'farm' ? 'farm' : 'interop',
    hub: props.g9Hub ?? '',
    lynxVersion: props.g9LynxVersion ?? '?',
    primjsVersion: props.g9PrimjsVersion ?? '?',
    device: props.g9Device ?? '?',
  };
}

export function App() {
  const [lines, setLines] = useState<string[]>([]);
  const [status, setStatus] = useState<'RUNNING' | 'PASS' | 'FAIL'>('RUNNING');

  useEffect(() => {
    runDeviceTest(readConfig(), {
      line: (text) => setLines((previous) => [...previous, text]),
      done: (pass) => setStatus(pass ? 'PASS' : 'FAIL'),
    });
  }, []);

  const color = status === 'PASS' ? '#1b7f3b' : status === 'FAIL' ? '#b3261e' : '#8a6d00';
  return (
    <view style={{ width: '100%', height: '100%', backgroundColor: '#ffffff', paddingTop: '36px' }}>
      <view style={{ padding: '8px', backgroundColor: color }}>
        <text style={{ color: '#ffffff', fontSize: '18px', fontWeight: 'bold' }}>{`G9SyncData on Lynx: ${status}`}</text>
      </view>
      <scroll-view scroll-orientation="vertical" style={{ flex: 1, padding: '6px' }}>
        {lines.map((line, index) => (
          <text key={index} style={{ fontSize: '9px', color: '#202020', marginBottom: '3px' }}>
            {line}
          </text>
        ))}
      </scroll-view>
    </view>
  );
}
