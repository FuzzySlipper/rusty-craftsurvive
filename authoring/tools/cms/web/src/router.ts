export type Route =
  | { page: 'overview' }
  | { page: 'listing'; category: string }
  | { page: 'record'; id: string }
  | { page: 'provenance' }
  | { page: 'search'; term: string }
  | { page: 'atlas'; ref?: string };

const encode = (value: string): string => encodeURIComponent(value);

export const listingUrl = (category: string): string => `#${encode(category)}`;
export const recordUrl = (id: string): string => `#record/${encode(id)}`;
export const searchUrl = (term: string): string => `#search/${encode(term)}`;
export const atlasUrl = (ref?: string): string => (ref ? `#atlas/${encode(ref)}` : '#atlas');

export function parseHash(hash: string): Route {
  const [head, ...rest] = hash.replace(/^#/, '').split('/');
  const tail = decodeURIComponent(rest.join('/'));
  switch (head) {
    case '':
    case 'overview':
      return { page: 'overview' };
    case 'record':
      return { page: 'record', id: tail };
    case 'provenance':
      return { page: 'provenance' };
    case 'search':
      return { page: 'search', term: tail };
    case 'atlas':
      return { page: 'atlas', ref: tail || undefined };
    default:
      return { page: 'listing', category: decodeURIComponent(head) };
  }
}

/** The navigation entry a route belongs to: a record page highlights its category. */
export function activeSection(route: Route): string {
  switch (route.page) {
    case 'listing':
      return route.category;
    case 'record':
      return route.id.split('-')[0];
    default:
      return route.page;
  }
}
