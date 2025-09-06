export const TITLE_LIMIT = 256;
export const DESCRIPTION_LIMIT = 4096;
export const FIELD_NAME_LIMIT = 256;
export const FIELD_VALUE_LIMIT = 1024;
export const FIELD_COUNT_LIMIT = 25;
export const FOOTER_TEXT_LIMIT = 2048;
export const AUTHOR_NAME_LIMIT = 256;
export const TOTAL_CHAR_LIMIT = 6000;
export const BUTTON_LABEL_LIMIT = 80;
export const BUTTON_COUNT_LIMIT = 25;

function checkUrl(name, url) {
  if (url && !/^https?:\/\//i.test(url)) {
    return `Invalid ${name}`;
  }
  return null;
}

export function validateEmbed(dto, buttons = []) {
  let total = 0;
  if (dto.title) {
    if (dto.title.length > TITLE_LIMIT) return 'Title too long';
    total += dto.title.length;
  }
  if (dto.description) {
    if (dto.description.length > DESCRIPTION_LIMIT) return 'Description too long';
    total += dto.description.length;
  }
  if (dto.footerText) {
    if (dto.footerText.length > FOOTER_TEXT_LIMIT) return 'Footer too long';
    total += dto.footerText.length;
  }
  if (dto.authorName) {
    if (dto.authorName.length > AUTHOR_NAME_LIMIT) return 'Author name too long';
    total += dto.authorName.length;
  }
  if (dto.fields) {
    if (dto.fields.length > FIELD_COUNT_LIMIT) return 'Too many fields';
    for (const f of dto.fields) {
      if (f.name.length > FIELD_NAME_LIMIT) return 'Field name too long';
      if (f.value.length > FIELD_VALUE_LIMIT) return 'Field value too long';
      total += f.name.length + f.value.length;
    }
  }
  if (total > TOTAL_CHAR_LIMIT) return 'Embed too large';

  let err;
  if ((err = checkUrl('url', dto.url))) return err;
  if ((err = checkUrl('thumbnail url', dto.thumbnailUrl))) return err;
  if ((err = checkUrl('image url', dto.imageUrl))) return err;
  if ((err = checkUrl('provider url', dto.providerUrl))) return err;
  if ((err = checkUrl('footer icon url', dto.footerIconUrl))) return err;
  if ((err = checkUrl('author icon url', dto.authorIconUrl))) return err;
  if ((err = checkUrl('video url', dto.videoUrl))) return err;
  if (dto.authors) {
    for (const a of dto.authors) {
      if (a.name && a.name.length > AUTHOR_NAME_LIMIT) return 'Author name too long';
      if ((err = checkUrl('author url', a.url))) return err;
      if ((err = checkUrl('author icon url', a.iconUrl))) return err;
    }
  }
  if (buttons) {
    if (buttons.length > BUTTON_COUNT_LIMIT) return 'Too many buttons';
    for (const b of buttons) {
      if (b.label && b.label.length > BUTTON_LABEL_LIMIT) return 'Button label too long';
      if ((err = checkUrl('button url', b.url))) return err;
    }
  }
  return null;
}
