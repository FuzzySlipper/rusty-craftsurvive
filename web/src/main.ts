import { mountRustyApplication } from '@rusty-engine/application-host';
import { mountCraftSurviveUi } from './game-ui';
import './styles.css';

const root = document.querySelector<HTMLElement>('#app');
if (root === null) throw new Error('missing CraftSurvive application root');
const course = new URLSearchParams(location.search).get('course');
const gardenEnabled = course === 'garden' || course === 'ghost-plate';

void mountRustyApplication({
  root,
  initialInteractionMode: 'gameplay',
  loadingLabel: 'Starting CraftSurvive…',
  failureLabel: 'CraftSurvive could not start',
  ...(gardenEnabled ? { renderer: { fog: { color: 0x9eb6c4, near: 18, far: 52 } } } : {}),
  mountUi: mountCraftSurviveUi,
});
