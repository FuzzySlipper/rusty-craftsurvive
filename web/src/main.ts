import { mountRustyApplication } from '@rusty-engine/application-host';
import { mountCraftSurviveUi } from './game-ui';
import './styles.css';

const root = document.querySelector<HTMLElement>('#app');
if (root === null) throw new Error('missing CraftSurvive application root');

void mountRustyApplication({
  root,
  initialInteractionMode: 'gameplay',
  loadingLabel: 'Starting CraftSurvive…',
  failureLabel: 'CraftSurvive could not start',
  mountUi: mountCraftSurviveUi,
});
