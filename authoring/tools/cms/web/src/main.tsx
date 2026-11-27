import { render } from 'preact';
import { AppProvider } from './state';
import { App } from './App';

const root = document.getElementById('app');
if (root) {
  render(
    <AppProvider>
      <App />
    </AppProvider>,
    root,
  );
}
