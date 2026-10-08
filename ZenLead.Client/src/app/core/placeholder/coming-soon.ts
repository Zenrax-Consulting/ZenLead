import { Component } from '@angular/core';
import { ActivatedRoute } from '@angular/router';

@Component({
  selector: 'app-coming-soon',
  standalone: false,
  template: '<h1>{{ title }}</h1><p>Coming in Sprint {{ sprint }}.</p>'
})
export class ComingSoon {
  title: string;
  sprint: number;

  constructor(route: ActivatedRoute) {
    this.title = route.snapshot.data['title'];
    this.sprint = route.snapshot.data['sprint'];
  }
}
